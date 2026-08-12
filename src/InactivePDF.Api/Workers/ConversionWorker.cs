using System.Diagnostics;
using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
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
    ConversionMetrics metrics,
    ILogger<ConversionWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

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
        while (!stoppingToken.IsCancellationRequested)
        {
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
                    if (buffer.TryEnqueue(item)) continue;
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
            await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(ConversionWorkItem item, CancellationToken cancellationToken)
    {
        var owner = item.LeaseOwner ?? _leaseOwner;
        var attempt = item.Attempt + 1;
        var current = new JobStatus(
            item.Job.Id,
            item.Job.CorrelationId,
            item.Job.Operation,
            ConversionJobState.Running,
            item.Job.AcceptedAt,
            DateTimeOffset.UtcNow,
            attempt);
        using var activity = metrics.ActivitySource.StartActivity("inactivepdf.convert", ActivityKind.Internal);
        activity?.SetTag("job.id", item.Job.Id);
        activity?.SetTag("job.operation", item.Job.Operation.ToString());
        activity?.SetTag("job.attempt", attempt);

        using var leaseLost = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseLost.Token);
        var renewalTask = RenewLeaseAsync(item.Job.Id, owner, leaseLost, cancellationToken);
        try
        {
            await persistence.UpsertAsync(current, cancellationToken).ConfigureAwait(false);
            await using var concurrencyLease = await concurrencyGate.AcquireAsync(item, linkedCancellation.Token).ConfigureAwait(false);
            var output = await processor.ProcessAsync(item with { Attempt = attempt, LeaseOwner = owner }, linkedCancellation.Token).ConfigureAwait(false);
            await persistence.CompleteSuccessWithLeaseAsync(
                current with { State = ConversionJobState.Succeeded, UpdatedAt = DateTimeOffset.UtcNow, OutputPath = output },
                owner,
                cancellationToken).ConfigureAwait(false);
            metrics.RecordSucceeded();
            WorkerLog.Completed(logger, item.Job.Id, attempt, item.Request.Options.Profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SafeReleaseAsync(item with { Attempt = attempt, LeaseOwner = owner }, DateTimeOffset.UtcNow, "shutdown", "The service was shutting down before conversion completed.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            WorkerLog.LeaseLost(logger, item.Job.Id, attempt);
        }
        catch (ConversionLeaseLostException)
        {
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
                        current,
                        item with { Attempt = attempt, LeaseOwner = owner },
                        owner,
                        nextAttemptAt,
                        classification.Code,
                        classification.Message,
                        exception,
                        cancellationToken).ConfigureAwait(false);
                    metrics.RecordRetried();
                    WorkerLog.Retrying(logger, exception, item.Job.Id, attempt, nextAttemptAt);
                    return;
                }
                catch (ConversionLeaseLostException)
                {
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
                WorkerLog.DeadLettered(logger, exception, item.Job.Id, classification.Code);
            }
            catch (ConversionLeaseLostException)
            {
                WorkerLog.LeaseLost(logger, item.Job.Id, attempt);
            }
        }
        finally
        {
            leaseLost.Cancel();
            try { await renewalTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
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
            WorkerLog.LeaseReleaseFailed(logger, exception, item.Job.Id);
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

    [LoggerMessage(LogLevel.Warning, "Conversion job {JobId} failed on attempt {Attempt}; retry scheduled for {NextAttemptAt}")]
    public static partial void Retrying(ILogger logger, Exception exception, Guid jobId, int attempt, DateTimeOffset nextAttemptAt);

    [LoggerMessage(LogLevel.Warning, "Conversion job {JobId} lost its durable lease on attempt {Attempt}")]
    public static partial void LeaseLost(ILogger logger, Guid jobId, int attempt);

    [LoggerMessage(LogLevel.Error, "Conversion job {JobId} dead-lettered with code {ErrorCode}")]
    public static partial void DeadLettered(ILogger logger, Exception exception, Guid jobId, string errorCode);

    [LoggerMessage(LogLevel.Error, "Conversion job {JobId} could not release its durable lease during shutdown")]
    public static partial void LeaseReleaseFailed(ILogger logger, Exception exception, Guid jobId);
}
