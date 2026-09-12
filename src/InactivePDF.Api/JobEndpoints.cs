using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Capabilities;
using InactivePDF.Application.Models;
using InactivePDF.Application.Policies;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Resources;
using InactivePDF.Infrastructure.Validation;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Domain.Contracts;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace InactivePDF.Api;

internal static class JobEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/jobs", async (HttpRequest request, IConversionJobCoordinator coordinator, IJobPersistence persistence, ConversionMetrics metrics, WorkspaceOptions workspaceOptions, IBoundedStreamCopier copier, ApiRequestLimits limits, ApiAdmissionService admission, WatermarkProfileStore watermarkProfiles, CancellationToken cancellationToken) =>
        {
            var identity = ApiAuthentication.GetIdentity(request.HttpContext);
            var form = await request.ReadFormAsync(cancellationToken);
            if (form.Files.Count > limits.MaximumFiles)
                return ApiLimitResponses.TooLarge(request.HttpContext, "file_count_exceeded", $"A request cannot contain more than {limits.MaximumFiles} files.", "maximum files per request", limits.MaximumFiles);
            if (form.Files.Any(file => file.Length > limits.MaximumFileBytes))
                return ApiLimitResponses.TooLarge(request.HttpContext, "file_bytes_exceeded", $"Each file must be at most {limits.MaximumFileBytes} bytes.", "maximum file bytes", limits.MaximumFileBytes);
            string correlationId;
            try { correlationId = ResolveCorrelationId(request, form); }
            catch (InvalidDataException) { return Results.BadRequest(new { code = "idempotency_key_mismatch", message = "correlationId must match Idempotency-Key when both are supplied." }); }
            var configuredOperation = Environment.GetEnvironmentVariable("INACTIVEPDF_DEFAULT_OPERATION");
            var operation = Enum.TryParse<ConversionOperation>(form["operation"].ToString(), ignoreCase: true, out var parsed)
                ? parsed
                : Enum.TryParse(configuredOperation, ignoreCase: true, out ConversionOperation defaultOperation)
                    ? defaultOperation
                    : ConversionOperation.ConvertFile;
            var documentInputs = form.Files.Select(file => new DocumentInput(file.FileName, file.ContentType, file.Length)).ToList();
            if (operation == ConversionOperation.CreateTextPdf && documentInputs.Count == 0)
            {
                documentInputs.Add(new DocumentInput("body.txt", "text/plain", Encoding.UTF8.GetByteCount(form["text"].ToString())));
            }
            var profile = form["profile"].ToString();
            if (string.IsNullOrWhiteSpace(profile)) profile = Environment.GetEnvironmentVariable("INACTIVEPDF_DEFAULT_PROFILE") ?? "archive";
            WatermarkOptions? directWatermark;
            try { directWatermark = ConversionApiHelpers.ParseWatermark(form["watermark"].ToString()); }
            catch (JsonException) { return Results.BadRequest(new { code = "invalid_watermark", message = "The watermark JSON is invalid." }); }
            var resolvedWatermark = ConversionApiHelpers.ResolveWatermark(form["watermarkProfile"].ToString(), directWatermark, watermarkProfiles, out var watermarkError);
            if (watermarkError is not null) return watermarkError;
            var watermarkProfileName = directWatermark is null && !string.IsNullOrWhiteSpace(form["watermarkProfile"].ToString())
                ? form["watermarkProfile"].ToString().Trim()
                : null;
            var requestModel = new ConversionRequest(correlationId, operation, documentInputs, new ConversionOptions(profile, WatermarkProfile: watermarkProfileName, Watermark: resolvedWatermark));
            var requestInputBytes = documentInputs.Sum(input => input.Length);
            var requestFileCount = documentInputs.Count;
            var admissionFailure = admission.TryAdmit(
                identity,
                new ApiAdmissionRequest(
                    request.ContentLength ?? requestInputBytes,
                    requestInputBytes,
                    documentInputs.Count == 0 ? 0 : documentInputs.Max(input => input.Length),
                    requestFileCount,
                    Queued: true,
                    ExistingQueuedJobs: identity?.ApiKeyId is { } owner ? persistence.PendingCountForOwner(owner) : 0,
                    Format: documentInputs.Count == 0 ? "txt" : GetFormat(documentInputs)),
                out var admissionLease);
            if (admissionFailure is not null) return admissionFailure.ToResult(request.HttpContext);
            ConversionJob job;
            try { job = coordinator.Accept(requestModel) with { OwnerApiKeyId = identity?.ApiKeyId }; }
            catch (ConversionRequestValidationException exception)
            {
                admissionLease?.Dispose();
                return Results.ValidationProblem(exception.Errors.ToDictionary(error => error.Code, error => new[] { error.Message }));
            }

            var workspace = WorkspacePathSecurity.EnsureSafeChild(workspaceOptions.RootPath, Path.Combine(workspaceOptions.RootPath, job.Id.ToString("N")));
            Directory.CreateDirectory(workspace);
            var inputs = new List<StoredInput>(form.Files.Count);
            try
            {
                foreach (var file in form.Files)
                {
                    var safeName = Path.GetFileName(file.FileName);
                    var path = Path.Combine(workspace, $"{Guid.NewGuid():N}-{safeName}");
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var source = file.OpenReadStream();
                    var written = await copier.CopyAsync(source, output, limits.MaximumFileBytes, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    inputs.Add(new StoredInput(safeName, path, written, file.ContentType));
                }
                if (operation == ConversionOperation.CreateTextPdf && inputs.Count == 0)
                {
                    var path = Path.Combine(workspace, "body.txt");
                    var text = form["text"].ToString();
                    await File.WriteAllTextAsync(path, text, cancellationToken);
                    inputs.Add(new StoredInput("body.txt", path, new FileInfo(path).Length, "text/plain"));
                }

                var status = new JobStatus(job.Id, job.CorrelationId, job.Operation, ConversionJobState.Queued, job.AcceptedAt, DateTimeOffset.UtcNow, 0,
                    OwnerApiKeyId: job.OwnerApiKeyId, Source: "API", Format: GetFormat(inputs), IdempotencyExpiresAt: DateTimeOffset.UtcNow.AddHours(24),
                    Profile: profile, WatermarkProfile: watermarkProfileName);
                var item = new ConversionWorkItem(job, requestModel, inputs, workspace);
                for (var index = 0; index < inputs.Count; index++)
                    _ = InputFormatValidator.Validate(inputs[index].Path, inputs[index].FileName, inputs[index].ContentType, ConversionExecutionModeParser.FromEnvironment());
                var fingerprint = await ConversionRequestFingerprint.ComputeAsync(requestModel, inputs, cancellationToken).ConfigureAwait(false);
                var duplicate = await persistence.CreateIfAbsentAsync(status, item, fingerprint, cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                {
                    DeleteWorkspace(workspace);
                    admissionLease?.Dispose();
                    var decision = ApiPolicyEvaluator.EvaluateOwnedResource(identity, ApiKeyScopes.JobsRead, duplicate.OwnerApiKeyId);
                    if (decision is not ApiPolicyDecision.Allow) return ApiPolicyEvaluator.ToResult(decision);
                    return Results.Accepted($"/v1/jobs/{duplicate.JobId}", JobApiResponses.ToPublic(duplicate));
                }

                admissionLease?.CommitQueued(job.Id);
                metrics.RecordAccepted();
                return Results.Accepted($"/v1/jobs/{job.Id}", JobApiResponses.ToPublic(status));
            }
            catch (IdempotencyConflictException)
            {
                DeleteWorkspace(workspace);
                admissionLease?.Dispose();
                return Results.Conflict(new { code = "idempotency_conflict", message = "The idempotency key was already used with a different request.", correlationId });
            }
            catch (ConversionFormatException exception)
            {
                DeleteWorkspace(workspace);
                admissionLease?.Dispose();
                return Results.Json(new { code = exception.Code, message = ConversionFailureClassifier.Classify(exception).Message, correlationId }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch { DeleteWorkspace(workspace); admissionLease?.Dispose(); throw; }
        }).RequireScope(ApiKeyScopes.ConvertSubmit).DisableAntiforgery();

        app.MapGet("/v1/jobs", async (HttpRequest request, IJobStatusStore store, CancellationToken cancellationToken) =>
        {
            var query = request.Query;
            ConversionJobState? parsedState = null;
            if (query.TryGetValue("state", out var stateValue) && !string.IsNullOrWhiteSpace(stateValue))
            {
                if (!Enum.TryParse<ConversionJobState>(stateValue.ToString(), true, out var state))
                    return Results.BadRequest(new { code = "invalid_state", message = "state is not a recognized job state." });
                parsedState = state;
            }
            if (!TryReadDate(query["createdAfter"], out var createdAfter) || !TryReadDate(query["createdBefore"], out var createdBefore))
                return Results.BadRequest(new { code = "invalid_date", message = "createdAfter and createdBefore must be ISO-8601 timestamps." });
            var identity = ApiAuthentication.GetIdentity(request.HttpContext);
            JobPage page;
            try
            {
                page = await store.ListAsync(new JobQuery(
                    parsedState,
                    query["source"].ToString(),
                    query["format"].ToString(),
                    createdAfter,
                    createdBefore,
                    query["cursor"].ToString(),
                    int.TryParse(query["limit"], CultureInfo.InvariantCulture, out var limit) ? limit : 50,
                    identity?.Kind == ApiIdentityKind.Integration ? identity.ApiKeyId : null), cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { code = "invalid_cursor", message = "The cursor is invalid." });
            }
            return Results.Ok(new { items = page.Jobs.Select(JobApiResponses.ToPublic), nextCursor = page.NextCursor });
        }).RequireScope(ApiKeyScopes.JobsRead);

        app.MapGet("/v1/jobs/{jobId:guid}", async (Guid jobId, HttpContext context, IJobStatusStore store, ApiAdmissionService admission, CancellationToken cancellationToken) =>
        {
            var status = await store.GetAsync(jobId, cancellationToken);
            var decision = ApiPolicyEvaluator.EvaluateOwnedResource(ApiAuthentication.GetIdentity(context), ApiKeyScopes.JobsRead, status?.OwnerApiKeyId);
            return decision is not ApiPolicyDecision.Allow ? ApiPolicyEvaluator.ToResult(decision) : status is null || admission.IsJobExpired(status.OwnerApiKeyId, status.AcceptedAt) ? Results.NotFound() : Results.Ok(JobApiResponses.ToPublic(status));
        }).RequireScope(ApiKeyScopes.JobsRead);

        app.MapPost("/v1/jobs/{jobId:guid}/cancel", async (Guid jobId, HttpContext context, IJobStatusStore store, IJobPersistence persistence, JobCancellationRegistry cancellations, CancellationToken cancellationToken) =>
        {
            var status = await store.GetAsync(jobId, cancellationToken);
            var decision = ApiPolicyEvaluator.EvaluateOwnedResource(ApiAuthentication.GetIdentity(context), ApiKeyScopes.JobsCancel, status?.OwnerApiKeyId);
            if (decision is not ApiPolicyDecision.Allow) return ApiPolicyEvaluator.ToResult(decision);
            var cancelled = await persistence.CancelAsync(jobId, DateTimeOffset.UtcNow, cancellationToken);
            if (cancelled is null) return Results.NotFound();
            if (cancelled.State == ConversionJobState.Cancelled) cancellations.Cancel(jobId);
            return Results.Ok(JobApiResponses.ToPublic(cancelled));
        }).RequireScope(ApiKeyScopes.JobsCancel);

        app.MapPost("/v1/jobs/{jobId:guid}/retry", async (Guid jobId, HttpContext context, IJobStatusStore store, IJobPersistence persistence, JobCancellationRegistry cancellations, WorkspaceOptions workspaceOptions, CancellationToken cancellationToken) =>
        {
            var status = await store.GetAsync(jobId, cancellationToken);
            var decision = ApiPolicyEvaluator.EvaluateOwnedResource(ApiAuthentication.GetIdentity(context), ApiKeyScopes.JobsCancel, status?.OwnerApiKeyId);
            if (decision is not ApiPolicyDecision.Allow) return ApiPolicyEvaluator.ToResult(decision);
            if (status is null) return Results.NotFound();
            if (status.State is not (ConversionJobState.Failed or ConversionJobState.DeadLettered or ConversionJobState.Interrupted))
                return Results.Conflict(new { code = "job_not_retryable", message = "Only failed, dead-lettered, or interrupted jobs can be retried." });
            try
            {
                var deadLetter = await persistence.GetDeadLetterAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (deadLetter is not null)
                {
                    WorkspacePathSecurity.EnsureSafeChild(workspaceOptions.RootPath, deadLetter.WorkItem.WorkspacePath);
                    foreach (var input in deadLetter.WorkItem.Inputs)
                        WorkspacePathSecurity.EnsureSafeChild(workspaceOptions.RootPath, input.Path);
                }
                cancellations.Reset(jobId);
                var retried = await persistence.RetryAsync(jobId, DateTimeOffset.UtcNow, cancellationToken);
                return retried is null ? Results.NotFound() : Results.Accepted($"/v1/jobs/{jobId:D}", JobApiResponses.ToPublic(retried));
            }
            catch (FileNotFoundException)
            {
                return Results.Problem("The original input is no longer available for retry.", statusCode: StatusCodes.Status410Gone);
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { code = "job_not_retryable", message = "The job cannot be retried in its current state." });
            }
        }).RequireScope(ApiKeyScopes.JobsCancel);

        app.MapDelete("/v1/jobs/{jobId:guid}", async (Guid jobId, HttpContext context, IJobStatusStore store, IJobPersistence persistence, JobCancellationRegistry cancellations, WorkspaceOptions workspaceOptions, CancellationToken cancellationToken) =>
        {
            var status = await store.GetAsync(jobId, cancellationToken);
            var decision = ApiPolicyEvaluator.EvaluateOwnedResource(ApiAuthentication.GetIdentity(context), ApiKeyScopes.JobsCancel, status?.OwnerApiKeyId);
            if (decision is not ApiPolicyDecision.Allow) return ApiPolicyEvaluator.ToResult(decision);
            if (status is null) return Results.NotFound();
            if (status.State is not (ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Cancelled or ConversionJobState.DeadLettered or ConversionJobState.Interrupted))
                return Results.Conflict(new { code = "job_active", message = "Active jobs must be cancelled before they can be deleted." });
            var removed = await persistence.DeleteAsync(jobId, cancellationToken);
            if (removed is null) return Results.NotFound();
            cancellations.Complete(jobId);
            DeleteJobWorkspace(workspaceOptions.RootPath, jobId);
            return Results.NoContent();
        }).RequireScope(ApiKeyScopes.JobsCancel);

        app.MapGet("/v1/jobs/{jobId:guid}/output", async (Guid jobId, HttpContext context, IJobStatusStore store, ApiAdmissionService admission, CancellationToken cancellationToken) =>
        {
            var status = await store.GetAsync(jobId, cancellationToken);
            var decision = ApiPolicyEvaluator.EvaluateOwnedResource(ApiAuthentication.GetIdentity(context), ApiKeyScopes.OutputsRead, status?.OwnerApiKeyId);
            return decision is not ApiPolicyDecision.Allow ? ApiPolicyEvaluator.ToResult(decision) : status is null || admission.IsJobExpired(status.OwnerApiKeyId, status.AcceptedAt) ? Results.NotFound() : status.OutputPath is { } output && File.Exists(output)
                ? Results.File(output, "application/pdf", "converted.pdf")
                : Results.NotFound();
        }).RequireScope(ApiKeyScopes.OutputsRead);

        app.MapGet("/v1/dead-letters", async (int? limit, IJobPersistence persistence, CancellationToken cancellationToken) =>
        {
            var maximumItems = Math.Clamp(limit ?? 100, 1, 1_000);
            var records = await persistence.ListDeadLettersAsync(maximumItems, cancellationToken).ConfigureAwait(false);
            return Results.Ok(records.Select(ToDeadLetterResponse));
        }).RequireAdministrator();

        app.MapPost("/v1/dead-letters/{jobId:guid}/replay", async (
            Guid jobId,
            HttpRequest request,
            IJobPersistence persistence,
            IConversionJobIdGenerator jobIdGenerator,
            IClock clock,
            WorkspaceOptions workspaceOptions,
            ResourcePolicy resourcePolicy,
            IBoundedStreamCopier copier,
            CancellationToken cancellationToken) =>
        {
            var deadLetter = await persistence.GetDeadLetterAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (deadLetter is null) return Results.NotFound();
            if (deadLetter.WorkItem.Inputs.Any(input => !File.Exists(input.Path)))
            {
                return Results.Problem(
                    "The original dead-letter input is no longer available for replay.",
                    statusCode: StatusCodes.Status410Gone);
            }

            var replayRequest = request.ContentLength is > 0
                ? await request.ReadFromJsonAsync<DeadLetterReplayRequest>(cancellationToken).ConfigureAwait(false)
                : null;
            var correlationId = string.IsNullOrWhiteSpace(replayRequest?.CorrelationId)
                ? $"replay-{jobId:N}-{Guid.NewGuid():N}"
                : replayRequest.CorrelationId.Trim();
            var acceptedAt = clock.UtcNow;
            var replayJob = deadLetter.WorkItem.Job with
            {
                Id = jobIdGenerator.Create(),
                CorrelationId = correlationId,
                State = ConversionJobState.Queued,
                AcceptedAt = acceptedAt
            };
            var replayWork = deadLetter.WorkItem with
            {
                Job = replayJob,
                Request = deadLetter.WorkItem.Request with { CorrelationId = correlationId },
                Attempt = 0,
                LeaseOwner = null
            };

            var replayWorkspace = Path.Combine(workspaceOptions.RootPath, replayJob.Id.ToString("N"));
            try
            {
                var replayInputs = await CopyReplayInputsAsync(
                    deadLetter.WorkItem.Inputs,
                    replayWorkspace,
                    resourcePolicy,
                    copier,
                    cancellationToken).ConfigureAwait(false);
                replayWork = replayWork with { Inputs = replayInputs, WorkspacePath = replayWorkspace };
                var status = new JobStatus(
                    replayJob.Id,
                    replayJob.CorrelationId,
                    replayJob.Operation,
                    ConversionJobState.Queued,
                    replayJob.AcceptedAt,
                    acceptedAt,
                    0,
                    OwnerApiKeyId: replayJob.OwnerApiKeyId,
                    Source: "API",
                    Format: GetFormat(replayWork.Inputs),
                    IdempotencyExpiresAt: acceptedAt.AddHours(24));
                var fingerprint = await ConversionRequestFingerprint.ComputeAsync(replayWork.Request, replayWork.Inputs, cancellationToken).ConfigureAwait(false);
                var duplicate = await persistence.CreateIfAbsentAsync(status, replayWork, fingerprint, cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                {
                    return Results.Conflict(new
                    {
                        code = "replay_correlation_exists",
                        message = "A replay must use a new idempotency identity.",
                        correlationId
                    });
                }

                return Results.Accepted($"/v1/jobs/{replayJob.Id}", JobApiResponses.ToPublic(status));
            }
            catch (Exception exception)
            {
                DeleteWorkspace(replayWorkspace);
                if (exception is OperationCanceledException) throw;
                return Results.Problem("The dead-letter job could not be replayed.", statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireAdministrator();

        app.MapGet("/v1/queue", (IConversionJobBuffer queue) => Results.Ok(new { queue.Count, queue.Capacity })).RequireAdministrator();
    }

    private static void DeleteWorkspace(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private static void DeleteJobWorkspace(string root, Guid jobId)
    {
        try
        {
            var path = WorkspacePathSecurity.EnsureSafeChild(root, Path.Combine(root, jobId.ToString("N")));
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ResolveCorrelationId(HttpRequest request, IFormCollection form)
    {
        var formValue = form["correlationId"].ToString().Trim();
        var headerValue = request.Headers["Idempotency-Key"].ToString().Trim();
        if (formValue.Length > 0 && headerValue.Length > 0 && !string.Equals(formValue, headerValue, StringComparison.Ordinal))
            throw new InvalidDataException("correlationId must match Idempotency-Key when both are supplied.");
        return headerValue.Length > 0 ? headerValue : formValue.Length > 0 ? formValue : Guid.NewGuid().ToString("N");
    }

    private static bool TryReadDate(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        return string.IsNullOrWhiteSpace(value) || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var valueParsed) && (parsed = valueParsed) is not null;
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

    private static async Task<IReadOnlyList<StoredInput>> CopyReplayInputsAsync(
        IReadOnlyList<StoredInput> originalInputs,
        string workspace,
        ResourcePolicy resourcePolicy,
        IBoundedStreamCopier copier,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace);
        var inputs = new List<StoredInput>(originalInputs.Count);
        foreach (var original in originalInputs)
        {
            var safeName = Path.GetFileName(original.FileName);
            var destinationPath = Path.Combine(workspace, $"{Guid.NewGuid():N}-{safeName}");
            await using var source = new FileStream(original.Path, FileMode.Open, FileAccess.Read, FileShare.Read, resourcePolicy.BufferSizeBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, resourcePolicy.BufferSizeBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = await copier.CopyAsync(source, destination, resourcePolicy.MaximumInputBytes, resourcePolicy.BufferSizeBytes, cancellationToken).ConfigureAwait(false);
            inputs.Add(new StoredInput(safeName, destinationPath, length, original.ContentType));
        }

        return inputs;
    }

    private static object ToDeadLetterResponse(DeadLetterRecord record) => new
    {
        record.JobId,
        record.CorrelationId,
        record.Operation,
        record.Attempt,
        record.ErrorCode,
        record.ErrorType,
        record.Exception,
        record.CreatedAt,
        record.AttemptHistory
    };

    private sealed record DeadLetterReplayRequest(string? CorrelationId);
}
