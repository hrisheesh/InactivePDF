using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Resources;

public sealed class FileSystemJobWorkspaceFactory(WorkspaceOptions options) : IJobWorkspaceFactory
{
    public async ValueTask<JobWorkspaceLease> CreateAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty job ID is required.", nameof(jobId));
        }

        var root = WorkspacePathSecurity.EnsureSafeChild(options.RootPath, Path.Combine(options.RootPath, jobId.ToString("N")));
        Directory.CreateDirectory(root);
        WorkspacePathSecurity.EnsureSafeChain(root, options.RootPath);
        var workspace = new JobWorkspace(
            jobId,
            root,
            Path.Combine(root, "input"),
            Path.Combine(root, "output"),
            Path.Combine(root, "temporary"),
            Path.Combine(root, "error"));

        Directory.CreateDirectory(workspace.InputPath);
        Directory.CreateDirectory(workspace.OutputPath);
        Directory.CreateDirectory(workspace.TemporaryPath);
        Directory.CreateDirectory(workspace.ErrorPath);
        WorkspacePathSecurity.EnsureSafeChain(workspace.InputPath, root);
        WorkspacePathSecurity.EnsureSafeChain(workspace.OutputPath, root);
        WorkspacePathSecurity.EnsureSafeChain(workspace.TemporaryPath, root);
        WorkspacePathSecurity.EnsureSafeChain(workspace.ErrorPath, root);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new FileSystemJobWorkspaceLease(workspace);
    }

    private sealed class FileSystemJobWorkspaceLease(JobWorkspace workspace) : JobWorkspaceLease
    {
        private int _disposed;

        public override JobWorkspace Workspace => workspace;

        public override ValueTask<WorkspaceCleanupResult> CleanupAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.FromResult(new WorkspaceCleanupResult(true, workspace.RootPath, null));
            }

            try
            {
                if (Directory.Exists(workspace.RootPath))
                {
                    Directory.Delete(workspace.RootPath, recursive: true);
                }

                return ValueTask.FromResult(new WorkspaceCleanupResult(true, workspace.RootPath, null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult(new WorkspaceCleanupResult(false, workspace.RootPath, exception.Message));
            }
        }
    }
}
