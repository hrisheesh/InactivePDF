namespace InactivePDF.Infrastructure.Watch;

public static class WatchFolderLayout
{
    public static void Ensure(WatchFolderOptions options)
    {
        Directory.CreateDirectory(options.RootPath);
        Directory.CreateDirectory(options.InputPath);
        Directory.CreateDirectory(options.OutputPath);
        Directory.CreateDirectory(options.OriginalsPath);
        Directory.CreateDirectory(options.ErrorsPath);
        Directory.CreateDirectory(options.ProcessingPath);
        Directory.CreateDirectory(options.LogPath);
    }
}
