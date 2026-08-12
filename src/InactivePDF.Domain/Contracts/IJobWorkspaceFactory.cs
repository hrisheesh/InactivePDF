using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface IJobWorkspaceFactory
{
    ValueTask<JobWorkspaceLease> CreateAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public abstract class JobWorkspaceLease : IAsyncDisposable
{
    public abstract JobWorkspace Workspace { get; }

    public abstract ValueTask<WorkspaceCleanupResult> CleanupAsync();

    public async ValueTask DisposeAsync()
    {
        _ = await CleanupAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
