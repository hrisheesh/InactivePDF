using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Capabilities;
using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Resources;
using InactivePDF.Infrastructure.Validation;
using InactivePDF.Domain.Contracts;
using System.Text;
using System.Text.Json;

namespace InactivePDF.Api;

internal static class JobEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/jobs", async (HttpRequest request, IConversionJobCoordinator coordinator, IJobPersistence persistence, ConversionMetrics metrics, WorkspaceOptions workspaceOptions, IBoundedStreamCopier copier, ApiRequestLimits limits, CancellationToken cancellationToken) =>
        {
            var form = await request.ReadFormAsync(cancellationToken);
            if (form.Files.Count > limits.MaximumFiles)
                return Results.Problem($"A request cannot contain more than {limits.MaximumFiles} files.", statusCode: StatusCodes.Status413PayloadTooLarge);
            if (form.Files.Any(file => file.Length > limits.MaximumFileBytes))
                return Results.Problem($"Each file must be at most {limits.MaximumFileBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
            var correlationId = form["correlationId"].ToString();
            if (string.IsNullOrWhiteSpace(correlationId)) correlationId = request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : Guid.NewGuid().ToString("N");
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
            var watermarkProfile = form["watermarkProfile"].ToString();
            var watermark = ParseWatermark(form["watermark"].ToString());
            var requestModel = new ConversionRequest(correlationId, operation, documentInputs, new ConversionOptions(profile, WatermarkProfile: string.IsNullOrWhiteSpace(watermarkProfile) ? null : watermarkProfile, Watermark: watermark));
            ConversionJob job;
            try { job = coordinator.Accept(requestModel); }
            catch (ConversionRequestValidationException exception) { return Results.ValidationProblem(exception.Errors.ToDictionary(error => error.Code, error => new[] { error.Message })); }

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

                var status = new JobStatus(job.Id, job.CorrelationId, job.Operation, ConversionJobState.Accepted, job.AcceptedAt, DateTimeOffset.UtcNow, 0);
                var item = new ConversionWorkItem(job, requestModel, inputs, workspace);
                for (var index = 0; index < inputs.Count; index++)
                    _ = InputFormatValidator.Validate(inputs[index].Path, inputs[index].FileName, inputs[index].ContentType);
                var fingerprint = await ConversionRequestFingerprint.ComputeAsync(requestModel, inputs, cancellationToken).ConfigureAwait(false);
                var duplicate = await persistence.CreateIfAbsentAsync(status, item, fingerprint, cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                {
                    DeleteWorkspace(workspace);
                    return Results.Accepted($"/v1/jobs/{duplicate.JobId}", duplicate);
                }

                metrics.RecordAccepted();
                return Results.Accepted($"/v1/jobs/{job.Id}", status);
            }
            catch (IdempotencyConflictException exception)
            {
                DeleteWorkspace(workspace);
                return Results.Conflict(new { code = "idempotency_conflict", message = exception.Message, correlationId });
            }
            catch (ConversionFormatException exception)
            {
                DeleteWorkspace(workspace);
                return Results.Json(new { code = exception.Code, message = exception.Message, correlationId }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch { DeleteWorkspace(workspace); throw; }
        }).DisableAntiforgery();

        app.MapGet("/v1/jobs/{jobId:guid}", async (Guid jobId, IJobStatusStore store, CancellationToken cancellationToken) =>
            await store.GetAsync(jobId, cancellationToken) is { } status ? Results.Ok(status) : Results.NotFound());

        app.MapGet("/v1/jobs/{jobId:guid}/output", async (Guid jobId, IJobStatusStore store, CancellationToken cancellationToken) =>
        {
            var status = await store.GetAsync(jobId, cancellationToken);
            return status?.OutputPath is { } output && File.Exists(output)
                ? Results.File(output, "application/pdf", "converted.pdf")
                : Results.NotFound();
        });

        app.MapGet("/v1/dead-letters", async (int? limit, IJobPersistence persistence, CancellationToken cancellationToken) =>
        {
            var maximumItems = Math.Clamp(limit ?? 100, 1, 1_000);
            var records = await persistence.ListDeadLettersAsync(maximumItems, cancellationToken).ConfigureAwait(false);
            return Results.Ok(records.Select(ToDeadLetterResponse));
        });

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
                State = ConversionJobState.Accepted,
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
                    ConversionJobState.Accepted,
                    replayJob.AcceptedAt,
                    acceptedAt,
                    0);
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

                return Results.Accepted($"/v1/jobs/{replayJob.Id}", status);
            }
            catch (Exception exception)
            {
                DeleteWorkspace(replayWorkspace);
                if (exception is OperationCanceledException) throw;
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapGet("/v1/queue", (IConversionJobBuffer queue) => Results.Ok(new { queue.Count, queue.Capacity }));
    }

    private static WatermarkOptions? ParseWatermark(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<WatermarkOptions>(value);

    private static void DeleteWorkspace(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

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
