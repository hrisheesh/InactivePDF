using InactivePDF.Application.Models;

namespace InactivePDF.Application.Abstractions;

public interface IJobStatusStore
{
    Task<JobStatus?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<JobStatus?> FindByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default);
    Task UpsertAsync(JobStatus status, CancellationToken cancellationToken = default);
}
