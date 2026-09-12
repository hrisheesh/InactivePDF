using System.Diagnostics;
using System.Text.Json;
using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Capabilities;
using InactivePDF.Application.Models;
using InactivePDF.Application.Policies;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Resources;
using InactivePDF.Infrastructure.Validation;

namespace InactivePDF.Api;

internal static class ConversionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/conversions", HandleAsync)
            .RequireScope(ApiKeyScopes.ConvertSubmit)
            .DisableAntiforgery();
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IConversionJobCoordinator coordinator,
        IJobPersistence persistence,
        ConversionMetrics metrics,
        WorkspaceOptions workspaceOptions,
        IBoundedStreamCopier copier,
        ApiRequestLimits limits,
        ApiAdmissionService admission,
        IsolatedConversionService service,
        WatermarkProfileStore watermarkProfiles,
        CancellationToken cancellationToken)
    {
        IFormCollection form;
        try { form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { return Results.BadRequest(new { code = "invalid_multipart_request", message = "The multipart request could not be read." }); }

        var mode = form["mode"].ToString();
        if (!ConversionApiHelpers.IsAsync(mode) && !ConversionApiHelpers.IsSync(mode))
            return Results.BadRequest(new { code = "invalid_mode", message = "mode must be 'sync' or 'async'." });
        if (form.Files.Count == 0)
            return Results.BadRequest(new { code = "file_required", message = "Send at least one file in the multipart 'file' field." });
        if (form.Files.Count > limits.MaximumFiles)
            return ApiLimitResponses.TooLarge(request.HttpContext, "file_count_exceeded", $"A request cannot contain more than {limits.MaximumFiles} files.", "maximum files per request", limits.MaximumFiles);
        if (form.Files.Any(file => file.Length > limits.MaximumFileBytes))
            return ApiLimitResponses.TooLarge(request.HttpContext, "file_bytes_exceeded", $"Each file must be at most {limits.MaximumFileBytes} bytes.", "maximum file bytes", limits.MaximumFileBytes);

        var operationText = form["operation"].ToString();
        if (!string.IsNullOrWhiteSpace(operationText) && !string.Equals(operationText, "ConvertFile", StringComparison.OrdinalIgnoreCase) && !string.Equals(operationText, "ConvertAndMerge", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { code = "invalid_operation", message = "operation must be ConvertFile or ConvertAndMerge." });
        var operation = form.Files.Count > 1 || string.Equals(operationText, "ConvertAndMerge", StringComparison.OrdinalIgnoreCase)
            ? ConversionOperation.ConvertAndMerge
            : ConversionOperation.ConvertFile;

        WatermarkOptions? directWatermark;
        try { directWatermark = ConversionApiHelpers.ParseWatermark(form["watermark"].ToString()); }
        catch (JsonException) { return Results.BadRequest(new { code = "invalid_watermark", message = "The watermark JSON is invalid." }); }
        var selectedWatermark = ConversionApiHelpers.ResolveWatermark(form["watermarkProfile"].ToString(), directWatermark, watermarkProfiles, out var watermarkError);
        if (watermarkError is not null) return watermarkError;

        var profile = string.IsNullOrWhiteSpace(form["profile"].ToString())
            ? Environment.GetEnvironmentVariable("INACTIVEPDF_DEFAULT_PROFILE") ?? "archive"
            : form["profile"].ToString().Trim();
        var watermarkProfileName = directWatermark is null && !string.IsNullOrWhiteSpace(form["watermarkProfile"].ToString())
            ? form["watermarkProfile"].ToString().Trim()
            : null;
        var inputs = form.Files.Select(file => new DocumentInput(file.FileName, file.ContentType, file.Length)).ToArray();
        string correlationId;
        try { correlationId = ResolveCorrelationId(request, form); }
        catch (InvalidDataException) { return Results.BadRequest(new { code = "idempotency_key_mismatch", message = "correlationId must match Idempotency-Key when both are supplied." }); }
        var requestModel = new ConversionRequest(
            correlationId,
            operation,
            inputs,
            new ConversionOptions(profile, WatermarkProfile: watermarkProfileName, Watermark: selectedWatermark));
        var identity = ApiAuthentication.GetIdentity(request.HttpContext);
        var inputBytes = inputs.Sum(input => input.Length);
        var admissionFailure = admission.TryAdmit(
            identity,
            new ApiAdmissionRequest(
                request.ContentLength ?? inputBytes,
                inputBytes,
                inputs.Max(input => input.Length),
                inputs.Length,
                Queued: ConversionApiHelpers.IsAsync(mode),
                ExistingQueuedJobs: identity?.ApiKeyId is { } owner ? persistence.PendingCountForOwner(owner) : 0,
                Format: GetFormat(inputs)),
            out var admissionLease);
        if (admissionFailure is not null) return admissionFailure.ToResult(request.HttpContext);

        if (ConversionApiHelpers.IsSync(mode))
            return await ConvertSynchronouslyAsync(request, form.Files, profile, operation, selectedWatermark, service, admission, admissionLease, inputBytes, cancellationToken).ConfigureAwait(false);

        ConversionJob job;
        try { job = coordinator.Accept(requestModel) with { OwnerApiKeyId = identity?.ApiKeyId }; }
        catch (ConversionRequestValidationException exception)
        {
            admissionLease?.Dispose();
            return Results.ValidationProblem(exception.Errors.ToDictionary(error => error.Code, error => new[] { error.Message }));
        }

        var workspace = WorkspacePathSecurity.EnsureSafeChild(workspaceOptions.RootPath, Path.Combine(workspaceOptions.RootPath, job.Id.ToString("N")));
        Directory.CreateDirectory(workspace);
        try
        {
            var storedInputs = new List<StoredInput>(form.Files.Count);
            foreach (var file in form.Files)
            {
                var safeName = Path.GetFileName(file.FileName);
                var path = Path.Combine(workspace, $"{Guid.NewGuid():N}-{safeName}");
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var source = file.OpenReadStream();
                var written = await copier.CopyAsync(source, output, limits.MaximumFileBytes, 64 * 1024, cancellationToken).ConfigureAwait(false);
                storedInputs.Add(new StoredInput(safeName, path, written, file.ContentType));
            }

            foreach (var input in storedInputs)
                _ = InputFormatValidator.Validate(input.Path, input.FileName, input.ContentType, ConversionExecutionModeParser.FromEnvironment());
            var status = new JobStatus(job.Id, job.CorrelationId, operation, ConversionJobState.Queued, job.AcceptedAt, DateTimeOffset.UtcNow, 0,
                OwnerApiKeyId: job.OwnerApiKeyId, Source: "API", Format: GetFormat(storedInputs), IdempotencyExpiresAt: DateTimeOffset.UtcNow.AddHours(24),
                Profile: profile, WatermarkProfile: watermarkProfileName);
            var work = new ConversionWorkItem(job, requestModel, storedInputs, workspace);
            var fingerprint = await ConversionRequestFingerprint.ComputeAsync(requestModel, storedInputs, cancellationToken).ConfigureAwait(false);
            var duplicate = await persistence.CreateIfAbsentAsync(status, work, fingerprint, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                DeleteWorkspace(workspace);
                admissionLease?.Dispose();
                var decision = ApiPolicyEvaluator.EvaluateOwnedResource(identity, ApiKeyScopes.JobsRead, duplicate.OwnerApiKeyId);
                return decision is ApiPolicyDecision.Allow
                    ? Results.Accepted($"/v1/jobs/{duplicate.JobId}", JobApiResponses.ToPublic(duplicate))
                    : ApiPolicyEvaluator.ToResult(decision);
            }

            admissionLease?.CommitQueued(job.Id);
            metrics.RecordAccepted();
            return Results.Accepted($"/v1/jobs/{job.Id}", JobApiResponses.ToPublic(status));
        }
        catch (ConversionFormatException exception)
        {
            DeleteWorkspace(workspace);
            admissionLease?.Dispose();
            return Results.Json(new { code = exception.Code, message = ConversionFailureClassifier.Classify(exception).Message, correlationId = job.CorrelationId }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (IdempotencyConflictException)
        {
            DeleteWorkspace(workspace);
            admissionLease?.Dispose();
            return Results.Conflict(new { code = "idempotency_conflict", message = "The idempotency key was already used with a different request.", correlationId = job.CorrelationId });
        }
        catch
        {
            DeleteWorkspace(workspace);
            admissionLease?.Dispose();
            throw;
        }
    }

    private static async Task<IResult> ConvertSynchronouslyAsync(
        HttpRequest request,
        IFormFileCollection files,
        string profile,
        ConversionOperation operation,
        WatermarkOptions? watermark,
        IsolatedConversionService service,
        ApiAdmissionService admission,
        ApiAdmissionService.ApiAdmissionLease? admissionLease,
        long inputBytes,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            IsolatedConversionOutput output;
            if (operation == ConversionOperation.ConvertAndMerge)
            {
                var inputs = files.Select(file => (file.OpenReadStream(), file.FileName, file.ContentType)).ToArray();
                try { output = await service.ConvertAndMergeAsync(inputs, profile, watermark: watermark, cancellationToken: cancellationToken).ConfigureAwait(false); }
                finally { foreach (var input in inputs) input.Item1.Dispose(); }
            }
            else
            {
                var file = files[0];
                await using var input = file.OpenReadStream();
                output = await service.ConvertFileAsync(input, file.FileName, file.ContentType, profile, watermark: watermark, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var maximumOutputBytes = admission.GetMaximumOutputBytes(ApiAuthentication.GetIdentity(request.HttpContext)?.ApiKeyId);
            if (maximumOutputBytes is { } maximum && output.Length > maximum)
            {
                await output.DisposeAsync().ConfigureAwait(false);
                return ApiLimitResponses.TooLarge(request.HttpContext, "output_bytes_exceeded", $"The generated output is {output.Length} bytes; this key allows {maximum} bytes. Retrying will not help unless the output is smaller.", "maximum output bytes", maximum);
            }

            admissionLease?.CommitSuccess(inputBytes, output.Length, timer.Elapsed.TotalMilliseconds, output.Metrics?.CpuMilliseconds ?? 0, output.Metrics?.PeakProcessTreeMemoryBytes ?? 0);
            return new ConversionFileResult(output);
        }
        finally { admissionLease?.Dispose(); }
    }

    private static string ResolveCorrelationId(HttpRequest request, IFormCollection form)
    {
        var formValue = form["correlationId"].ToString().Trim();
        var headerValue = request.Headers["Idempotency-Key"].ToString().Trim();
        if (formValue.Length > 0 && headerValue.Length > 0 && !string.Equals(formValue, headerValue, StringComparison.Ordinal))
            throw new InvalidDataException("correlationId must match Idempotency-Key when both are supplied.");
        return headerValue.Length > 0 ? headerValue : formValue.Length > 0 ? formValue : Guid.NewGuid().ToString("N");
    }

    private static string GetFormat(IReadOnlyList<StoredInput> inputs)
    {
        var formats = inputs.Select(input => Path.GetExtension(input.FileName).TrimStart('.').ToLowerInvariant()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return formats.Length == 1 ? formats[0] : formats.Length == 0 ? "unknown" : "mixed";
    }

    private static string GetFormat(IEnumerable<DocumentInput> inputs)
    {
        var formats = inputs.Select(input => Path.GetExtension(input.FileName).TrimStart('.').ToLowerInvariant()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return formats.Length == 1 ? formats[0] : formats.Length == 0 ? "unknown" : "mixed";
    }

    private static void DeleteWorkspace(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
