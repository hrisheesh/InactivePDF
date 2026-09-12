using InactivePDF.Application.Models;

namespace InactivePDF.Application.Abstractions;

/// <summary>
/// Durable persistence boundary for accepted jobs, pending work, and terminal results.
/// Implementations must make job creation and pending-work creation atomic.
/// </summary>
public interface IJobPersistence : IJobStatusStore
{
    int PendingCountForOwner(Guid ownerApiKeyId);

    Task<JobStatus?> CreateIfAbsentAsync(
        JobStatus status,
        ConversionWorkItem pendingWork,
        CancellationToken cancellationToken = default);

    Task<JobStatus?> CreateIfAbsentAsync(
        JobStatus status,
        ConversionWorkItem pendingWork,
        string? requestFingerprint,
        CancellationToken cancellationToken = default);

    Task<JobStatus?> CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<JobStatus?> RetryAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<JobStatus?> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ConversionWorkItem> ReadPendingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversionWorkItem>> ClaimPendingAsync(
        string leaseOwner,
        int maximumItems,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        Guid jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(
        ConversionWorkItem work,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    Task ScheduleRetryAsync(
        JobStatus status,
        ConversionWorkItem work,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        string errorMessage,
        Exception exception,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadLetterRecord>> ListDeadLettersAsync(
        int maximumItems,
        CancellationToken cancellationToken = default);

    Task<DeadLetterRecord?> GetDeadLetterAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task RemovePendingAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task CompleteSuccessAsync(JobStatus status, CancellationToken cancellationToken = default);

    Task CompleteSuccessWithLeaseAsync(
        JobStatus status,
        string leaseOwner,
        CancellationToken cancellationToken = default);

    Task CompleteFailureAsync(
        JobStatus status,
        ConversionWorkItem work,
        Exception exception,
        CancellationToken cancellationToken = default);

    Task CompleteFailureWithLeaseAsync(
        JobStatus status,
        ConversionWorkItem work,
        Exception exception,
        string leaseOwner,
        string errorCode,
        CancellationToken cancellationToken = default);
}
