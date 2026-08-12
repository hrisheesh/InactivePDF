using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Resources;

public sealed class JobWorkspaceService(IJobWorkspaceFactory factory)
{
    public ValueTask<JobWorkspaceLease> CreateAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty job ID is required.", nameof(jobId));
        }

        return factory.CreateAsync(jobId, cancellationToken);
    }
}
