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

        var root = Path.GetFullPath(Path.Combine(options.RootPath, jobId.ToString("N")));
        EnsurePathChainIsSafe(options.RootPath);
        Directory.CreateDirectory(root);
        EnsurePathChainIsSafe(root);
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
        EnsurePathChainIsSafe(workspace.InputPath);
        EnsurePathChainIsSafe(workspace.OutputPath);
        EnsurePathChainIsSafe(workspace.TemporaryPath);
        EnsurePathChainIsSafe(workspace.ErrorPath);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new FileSystemJobWorkspaceLease(workspace);
    }

    private static void EnsureNoReparsePoint(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException($"Workspace path cannot be a symbolic link or reparse point: '{info.FullName}'.");
    }

    private static void EnsurePathChainIsSafe(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException($"Workspace path cannot be a symbolic link or reparse point: '{current.FullName}'.");
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
