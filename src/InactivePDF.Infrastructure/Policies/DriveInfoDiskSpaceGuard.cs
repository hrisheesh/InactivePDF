using InactivePDF.Domain.Contracts;

namespace InactivePDF.Infrastructure.Policies;

public sealed class DriveInfoDiskSpaceGuard : IDiskSpaceGuard
{
    public void EnsureAvailable(string path, long minimumFreeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"Unable to determine the drive for '{path}'.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.AvailableFreeSpace < minimumFreeBytes)
        {
            throw new IOException($"Insufficient free disk space on '{root}'.");
        }
    }
}
