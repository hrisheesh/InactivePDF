namespace InactivePDF.Infrastructure.Resources;

public static class WorkspacePathSecurity
{
    public static string EnsureSafeChild(string root, string child)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child);
        if (!fullChild.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The path must remain inside the configured workspace.");
        EnsureSafeChain(fullChild, fullRoot.TrimEnd(Path.DirectorySeparatorChar));
        return fullChild;
    }

    public static void EnsureSafeChain(string path, string boundary)
    {
        var boundaryPath = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException($"Workspace paths cannot contain a symbolic link or reparse point: '{current.FullName}'.");
            if (current.FullName.Equals(boundaryPath, StringComparison.OrdinalIgnoreCase)) return;
            current = current.Parent;
        }
        throw new UnauthorizedAccessException("The path is outside the configured workspace.");
    }
}
