using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Infrastructure.Watch;

public static class WatchFolderLayout
{
    public static void Ensure(WatchFolderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.RootPath);
        Directory.CreateDirectory(options.InputPath);
        Directory.CreateDirectory(options.OutputPath);
        Directory.CreateDirectory(options.OriginalsPath);
        Directory.CreateDirectory(options.ErrorsPath);
        Directory.CreateDirectory(options.ProcessingPath);
        Directory.CreateDirectory(options.LogPath);

        WorkspacePathSecurity.EnsureSafeChain(options.RootPath, options.RootPath);
        foreach (var path in new[] { options.InputPath, options.OutputPath, options.OriginalsPath, options.ErrorsPath, options.ProcessingPath, options.LogPath })
            WorkspacePathSecurity.EnsureSafeChain(path, options.RootPath);
    }
}
