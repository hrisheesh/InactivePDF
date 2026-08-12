namespace InactivePDF.Infrastructure.Watch;

public sealed record WatchFolderOptions(
    string RootPath,
    int MaximumConcurrentConversions = 2,
    int MaximumRetries = 3,
    TimeSpan ScanInterval = default,
    TimeSpan FileStabilityDelay = default,
    int MaximumHeavyConversions = 1,
    int MaximumMarkupConversions = 1)
{
    public string InputPath => Path.Combine(RootPath, "Input");
    public string OutputPath => Path.Combine(RootPath, "Output");
    public string OriginalsPath => Path.Combine(RootPath, "Originals");
    public string ErrorsPath => Path.Combine(RootPath, "Errors");
    public string ProcessingPath => Path.Combine(RootPath, "Processing");
    public string LogPath => Path.Combine(RootPath, "Logs");
    public TimeSpan EffectiveScanInterval => ScanInterval == default ? TimeSpan.FromSeconds(2) : ScanInterval;
    public TimeSpan EffectiveFileStabilityDelay => FileStabilityDelay == default ? TimeSpan.FromSeconds(2) : FileStabilityDelay;

    public static WatchFolderOptions FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("INACTIVEPDF_WATCH_ROOT") ??
            (OperatingSystem.IsWindows() ? @"C:\InactivePDF\WatchFolders\Default" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InactivePDF", "WatchFolders", "Default"));
        return new WatchFolderOptions(root,
            ParsePositive("INACTIVEPDF_WATCH_CONCURRENCY", 2),
            ParseNonNegative("INACTIVEPDF_WATCH_RETRIES", 1),
            ParseSeconds("INACTIVEPDF_WATCH_SCAN_INTERVAL_SECONDS", 2),
            ParseSeconds("INACTIVEPDF_WATCH_FILE_STABILITY_SECONDS", 2),
            ParsePositive("INACTIVEPDF_WATCH_HEAVY_CONCURRENCY", 1),
            ParsePositive("INACTIVEPDF_WATCH_MARKUP_CONCURRENCY", 1));
    }

    private static int ParsePositive(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    private static int ParseNonNegative(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
    private static TimeSpan ParseSeconds(string name, int fallback) => TimeSpan.FromSeconds(ParsePositive(name, fallback));
}
