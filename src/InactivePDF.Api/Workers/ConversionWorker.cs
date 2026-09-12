using System.Diagnostics;
using System.Collections.Concurrent;
using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Capabilities;
using InactivePDF.Application.Models;
using InactivePDF.Application.Policies;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InactivePDF.Api.Workers;

/// <summary>
/// Continuously dispatches durable pending work into a bounded in-memory buffer and
/// processes each claimed item under a renewable lease.
/// </summary>
public sealed class ConversionWorker(
    IConversionJobBuffer buffer,
    IJobPersistence persistence,
    IConversionWorkProcessor processor,
    ConversionWorkerOptions options,
    ConversionConcurrencyGate concurrencyGate,
    ApiAdmissionService admission,
    JobCancellationRegistry cancellations,
    ConversionMetrics metrics,
    ConversionTelemetryStore telemetry,
    ILogger<ConversionWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly string _workerId = $"worker-{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<Guid, ConversionWorkItem> _bufferedClaims = new();
    private readonly ConcurrentDictionary<Guid, byte> _expiredBufferedClaims = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dispatcher = DispatchAsync(stoppingToken);
        var consumers = Enumerable.Range(0, options.WorkerCount)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();
        try
        {
            await Task.WhenAll(consumers.Append(dispatcher)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await ReleaseBufferedWorkAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        var renewAt = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= renewAt)
            {
                foreach (var pair in _bufferedClaims)
                    if (!await persistence.RenewLeaseAsync(pair.Key, _leaseOwner, options.EffectiveJobLeaseDuration, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false) && _bufferedClaims.ContainsKey(pair.Key))
                        _expiredBufferedClaims[pair.Key] = 0;
                renewAt = DateTimeOffset.UtcNow + options.EffectiveJobLeaseDuration / 3;
            }
            var availableSlots = options.QueueCapacity - buffer.Count;
            if (availableSlots > 0)
            {
                var claims = await persistence.ClaimPendingAsync(
                    _leaseOwner,
                    availableSlots,
                    options.EffectiveJobLeaseDuration,
                    DateTimeOffset.UtcNow,
                    stoppingToken).ConfigureAwait(false);

                foreach (var item in claims)
                {
                    _bufferedClaims[item.Job.Id] = item;
                    if (buffer.TryEnqueue(item)) continue;
                    _bufferedClaims.TryRemove(item.Job.Id, out _);
                    await SafeReleaseAsync(item, DateTimeOffset.UtcNow, "dispatcher_backpressure", "The in-memory dispatch buffer was full.").ConfigureAwait(false);
                }

                if (claims.Count > 0) continue;
            }

            await Task.Delay(options.EffectiveDispatcherPollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ConversionWorkItem item;
            try { item = await buffer.DequeueAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            _bufferedClaims.TryRemove(item.Job.Id, out _);
            if (_expiredBufferedClaims.TryRemove(item.Job.Id, out _)) continue;
            if (!await persistence.RenewLeaseAsync(item.Job.Id, _leaseOwner, options.EffectiveJobLeaseDuration, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false)) continue;
            await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(ConversionWorkItem item, CancellationToken cancellationToken)
    {
        var owner = item.LeaseOwner ?? _leaseOwner;
        var attempt = item.Attempt + 1;
        var stored = await persistence.GetAsync(item.Job.Id, cancellationToken).ConfigureAwait(false);
        var inputBytes = item.Inputs.Sum(input => input.LengthBytes);
        var format = stored?.Format ?? ResolveFormat(item);
        var source = stored?.Source ?? "API";
        var lane = ResolveLane(item);
        var current = new JobStatus(
            item.Job.Id,
            item.Job.CorrelationId,
            item.Job.Operation,
            ConversionJobState.WaitingForResources,
            item.Job.AcceptedAt,
            DateTimeOffset.UtcNow,
            attempt,
            OwnerApiKeyId: item.Job.OwnerApiKeyId,
            Source: source,
            Format: format,
            ProgressPercent: 0,
            CurrentWorker: _workerId,
            CurrentLane: lane,
            QueueWaitMilliseconds: Math.Max(0, (long)(DateTimeOffset.UtcNow - item.Job.AcceptedAt).TotalMilliseconds),
            InputBytes: inputBytes,
            RetryCount: Math.Max(0, attempt - 1),
            IdempotencyExpiresAt: stored?.IdempotencyExpiresAt);
        using var activity = metrics.ActivitySource.StartActivity("inactivepdf.convert", ActivityKind.Internal);
        activity?.SetTag("job.id", item.Job.Id);
        activity?.SetTag("job.operation", item.Job.Operation.ToString());
        activity?.SetTag("job.attempt", attempt);

        using var leaseLost = new CancellationTokenSource();
        var jobCancellationToken = cancellations.Register(item.Job.Id);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseLost.Token, jobCancellationToken);
        var renewalTask = RenewLeaseAsync(item.Job.Id, owner, leaseLost, cancellationToken);
        var apiProcessingLease = ApiAdmissionService.ApiProcessingLease.Empty;
        var apiProcessingCompleted = false;
        var processingStartedAt = DateTimeOffset.UtcNow;
        var processingTimer = Stopwatch.StartNew();
        try
        {
            apiProcessingLease = await admission.BeginQueuedProcessingAsync(item.Job.Id, item.Job.OwnerApiKeyId, linkedCancellation.Token).ConfigureAwait(false);
            await persistence.UpsertAsync(current, cancellationToken).ConfigureAwait(false);
            await using var concurrencyLease = await concurrencyGate.AcquireAsync(item, linkedCancellation.Token).ConfigureAwait(false);
            processingStartedAt = DateTimeOffset.UtcNow;
            processingTimer.Restart();
            current = current with { State = ConversionJobState.Processing, UpdatedAt = processingStartedAt, ProgressPercent = 5 };
            await persistence.UpsertAsync(current, cancellationToken).ConfigureAwait(false);
            var output = await processor.ProcessAsync(item with { Attempt = attempt, LeaseOwner = owner }, linkedCancellation.Token).ConfigureAwait(false);
            var maximumOutputBytes = admission.GetMaximumOutputBytes(item.Job.OwnerApiKeyId);
            if (maximumOutputBytes is { } maximum && new FileInfo(output).Length > maximum)
                throw new ConversionResourceLimitException($"The generated output exceeds this API key's maximum output size of {maximum} bytes.");
            var outputBytes = new FileInfo(output).Length;
            var processingMilliseconds = Math.Max(0, (long)processingTimer.Elapsed.TotalMilliseconds);
            current = current with { ProgressPercent = 95, UpdatedAt = DateTimeOffset.UtcNow };
            await persistence.UpsertAsync(current, cancellationToken).ConfigureAwait(false);
            await persistence.CompleteSuccessWithLeaseAsync(
                current with
                {
                    State = ConversionJobState.Succeeded,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    OutputPath = output,
                    ProgressPercent = 100,
                    CurrentWorker = null,
                    CurrentLane = lane,
                    QueueWaitMilliseconds = Math.Max(0, (long)(processingStartedAt - item.Job.AcceptedAt).TotalMilliseconds),
                    ProcessingMilliseconds = processingMilliseconds,
                    InputBytes = inputBytes,
                    OutputBytes = outputBytes,
                    CompressionPercent = inputBytes == 0 ? null : (inputBytes - outputBytes) * 100d / inputBytes
                },
                owner,
                cancellationToken).ConfigureAwait(false);
            var observation = telemetry.LatestForJob(item.Job.Id);
            apiProcessingLease.Complete(succeeded: true, item.Inputs.Sum(input => input.LengthBytes), outputBytes, processingMilliseconds,
                observation?.WorkerCpuMilliseconds ?? 0, observation?.PeakProcessTreeMemoryBytes ?? 0);
            apiProcessingCompleted = true;
            metrics.RecordSucceeded();
            WorkerLog.Completed(logger, item.Job.Id, attempt, item.Request.Options.Profile);
        }
        catch (OperationCanceledException) when (jobCancellationToken.IsCancellationRequested)
        {
            apiProcessingCompleted = true;
            apiProcessingLease.Dispose();
            await persistence.UpsertAsync(current with
            {
                State = ConversionJobState.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = "cancelled",
                ErrorMessage = "The job was cancelled by the client.",
                CurrentWorker = null,
                CurrentLane = lane,
                ProcessingMilliseconds = Math.Max(0, (long)processingTimer.Elapsed.TotalMilliseconds),
                InputBytes = inputBytes
            }, CancellationToken.None).ConfigureAwait(false);
            await persistence.RemovePendingAsync(item.Job.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            apiProcessingCompleted = true;
            apiProcessingLease.Dispose();
            await SafeReleaseAsync(item with { Attempt = attempt, LeaseOwner = owner }, DateTimeOffset.UtcNow, "shutdown", "The service was shutting down before conversion completed.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            apiProcessingCompleted = true;
            apiProcessingLease.Dispose();
            WorkerLog.LeaseLost(logger, item.Job.Id, attempt);
        }
        catch (ConversionLeaseLostException)
        {
            apiProcessingCompleted = true;
            apiProcessingLease.Dispose();
            WorkerLog.LeaseLost(logger, item.Job.Id, attempt);
        }
        catch (Exception exception)
        {
            var classification = ConversionFailureClassifier.Classify(exception);
            if (classification.IsRetryable && attempt < options.MaximumAttempts)
            {
                var nextAttemptAt = DateTimeOffset.UtcNow + CalculateRetryDelay(item.Job.Id, attempt);
                try
                {
                    await persistence.ScheduleRetryAsync(
                        current with { RetryCount = attempt, UpdatedAt = DateTimeOffset.UtcNow },
                        item with { Attempt = attempt, LeaseOwner = owner },
                        owner,
                        nextAttemptAt,
                        classification.Code,
                        classification.Message,
                        exception,
                        cancellationToken).ConfigureAwait(false);
                    metrics.RecordRetried();
                    WorkerLog.Retrying(logger, classification.Code, item.Job.Id, attempt, nextAttemptAt);
                    apiProcessingCompleted = true;
                    apiProcessingLease.Requeue();
                    return;
                }
                catch (ConversionLeaseLostException)
                {
                    apiProcessingCompleted = true;
                    apiProcessingLease.Dispose();
                    WorkerLog.LeaseLost(logger, item.Job.Id, attempt);
                    return;
                }
            }

            try
            {
                await persistence.CompleteFailureWithLeaseAsync(
                    current with { State = ConversionJobState.Failed, UpdatedAt = DateTimeOffset.UtcNow },
                    item with { Attempt = attempt, LeaseOwner = owner },
                    exception,
                    owner,
                    classification.Code,
                    CancellationToken.None).ConfigureAwait(false);
                metrics.RecordFailed();
                metrics.RecordDeadLettered();
                WorkerLog.DeadLettered(logger, item.Job.Id, classification.Code);
                apiProcessingCompleted = true;
                apiProcessingLease.Dispose();
            }
            catch (ConversionLeaseLostException)
            {
                apiProcessingCompleted = true;
                apiProcessingLease.Dispose();
                WorkerLog.LeaseLost(logger, item.Job.Id, attempt);
            }
        }
        finally
        {
            if (!apiProcessingCompleted) apiProcessingLease.Dispose();
            leaseLost.Cancel();
            try { await renewalTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            cancellations.Complete(item.Job.Id);
        }
    }

    private static string ResolveFormat(ConversionWorkItem item)
    {
        var formats = item.Inputs.Select(input => Path.GetExtension(input.FileName).TrimStart('.').ToLowerInvariant()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return formats.Length == 1 ? formats[0] : formats.Length == 0 ? "unknown" : "mixed";
    }

    private static string ResolveLane(ConversionWorkItem item)
    {
        if (item.Request.Operation == ConversionOperation.CreateTextPdf) return "Text";
        if (item.Request.Operation is ConversionOperation.ConvertAndMerge or ConversionOperation.ConvertFiles) return "Pdf";
        var extension = item.Inputs.Count == 0 ? string.Empty : Path.GetExtension(item.Inputs[0].FileName);
        return SupportedFormatCatalog.TryGet(extension, out var format) ? format.Route switch
        {
            ConversionFormatRoute.Image => "Image",
            ConversionFormatRoute.PdfPassThrough => "Pdf",
            ConversionFormatRoute.TextGenerator => "Text",
            _ => "Office"
        } : "Office";
    }

    private async Task RenewLeaseAsync(Guid jobId, string owner, CancellationTokenSource leaseLost, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1_000, options.EffectiveJobLeaseDuration.TotalMilliseconds / 3));
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, leaseLost.Token);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(renewalCancellation.Token).ConfigureAwait(false))
            {
                if (!await persistence.RenewLeaseAsync(jobId, owner, options.EffectiveJobLeaseDuration, DateTimeOffset.UtcNow, renewalCancellation.Token).ConfigureAwait(false))
                {
                    leaseLost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested) { }
        catch
        {
            leaseLost.Cancel();
        }
    }

    private async Task ReleaseBufferedWorkAsync()
    {
        while (buffer.TryDequeue(out var item) && item is not null)
        {
            await SafeReleaseAsync(item, DateTimeOffset.UtcNow, "shutdown", "The service stopped before the job began.").ConfigureAwait(false);
        }
    }

    private async Task SafeReleaseAsync(ConversionWorkItem item, DateTimeOffset nextAttemptAt, string errorCode, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(item.LeaseOwner)) return;
        try
        {
            await persistence.ReleaseLeaseAsync(item, item.LeaseOwner, nextAttemptAt, errorCode, errorMessage, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ConversionLeaseLostException) { }
        catch (Exception exception)
        {
                WorkerLog.LeaseReleaseFailed(logger, exception.GetType().Name, item.Job.Id);
        }
    }

    private TimeSpan CalculateRetryDelay(Guid jobId, int attempt)
    {
        var exponent = Math.Min(6, Math.Max(0, attempt - 1));
        var baseMilliseconds = Math.Min(30_000, options.EffectiveRetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        var deterministicJitter = Math.Abs(jobId.GetHashCode()) % 251;
        return TimeSpan.FromMilliseconds(Math.Min(30_000, baseMilliseconds + deterministicJitter));
    }
}

internal static partial class WorkerLog
{
    [LoggerMessage(LogLevel.Information, "Conversion job {JobId} completed on attempt {Attempt} with profile {Profile}")]
    public static partial void Completed(ILogger logger, Guid jobId, int attempt, string profile);

    [LoggerMessage(LogLevel.Warning, "Conversion job {JobId} failed with code {ErrorCode} on attempt {Attempt}; retry scheduled for {NextAttemptAt}")]
    public static partial void Retrying(ILogger logger, string errorCode, Guid jobId, int attempt, DateTimeOffset nextAttemptAt);

    [LoggerMessage(LogLevel.Warning, "Conversion job {JobId} lost its durable lease on attempt {Attempt}")]
    public static partial void LeaseLost(ILogger logger, Guid jobId, int attempt);

    [LoggerMessage(LogLevel.Error, "Conversion job {JobId} dead-lettered with code {ErrorCode}")]
    public static partial void DeadLettered(ILogger logger, Guid jobId, string errorCode);

    [LoggerMessage(LogLevel.Error, "Conversion job {JobId} could not release its durable lease during shutdown; exceptionType={ExceptionType}")]
    public static partial void LeaseReleaseFailed(ILogger logger, string exceptionType, Guid jobId);
}
