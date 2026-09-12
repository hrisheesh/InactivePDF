namespace InactivePDF.Infrastructure.Resources;

public static class WorkspacePathSecurity
{
    public static string EnsureSafeChild(string root, string child)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(child);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullChild = Path.GetFullPath(child);
        var relative = Path.GetRelativePath(fullRoot, fullChild);
        if (relative.Equals("..", PathComparison) || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The path must remain inside the configured workspace.");
        EnsureSafeChain(fullChild, fullRoot);
        return fullChild;
    }

    public static void EnsureSafeChain(string path, string boundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        var boundaryPath = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentPath = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(currentPath))
        {
            var current = TryGetExistingEntry(currentPath);
            if (current is not null && IsReparsePoint(current))
                throw new UnauthorizedAccessException("Workspace paths cannot contain a symbolic link or reparse point.");
            if (currentPath.Equals(boundaryPath, PathComparison)) return;

            var parent = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(parent) || parent.Equals(currentPath, PathComparison)) break;
            currentPath = parent;
        }
        throw new UnauthorizedAccessException("The path is outside the configured workspace.");
    }

    private static FileSystemInfo? TryGetExistingEntry(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists || file.LinkTarget is not null) return file;

        var directory = new DirectoryInfo(path);
        return directory.Exists || directory.LinkTarget is not null ? directory : null;
    }

    private static bool IsReparsePoint(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
