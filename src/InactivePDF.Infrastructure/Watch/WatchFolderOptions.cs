namespace InactivePDF.Infrastructure.Watch;

public sealed record WatchFolderOptions(
    string RootPath,
    int MaximumConcurrentConversions = 2,
    int MaximumRetries = 3,
    TimeSpan ScanInterval = default,
    TimeSpan FileStabilityDelay = default,
    int MaximumHeavyConversions = 2,
    int MaximumMarkupConversions = 1,
    long ResourceBudgetBytes = 2L * 1024 * 1024 * 1024,
    long OfficeReservationBytes = 1_207_959_552,
    long ImageReservationBytes = 1_073_741_824,
    long MarkupReservationBytes = 268_435_456,
    long LargeFileReservationBytes = 67_108_864,
    string? WatermarkProfile = null)
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
            ParsePositive("INACTIVEPDF_WATCH_HEAVY_CONCURRENCY", 2),
            ParsePositive("INACTIVEPDF_WATCH_MARKUP_CONCURRENCY", 1),
            ParseBytes("INACTIVEPDF_WATCH_RESOURCE_BUDGET_BYTES", 2L * 1024 * 1024 * 1024),
            ParseBytes("INACTIVEPDF_WATCH_OFFICE_RESERVATION_BYTES", 1_207_959_552),
            ParseBytes("INACTIVEPDF_WATCH_IMAGE_RESERVATION_BYTES", 1_073_741_824),
            ParseBytes("INACTIVEPDF_WATCH_MARKUP_RESERVATION_BYTES", 268_435_456),
            ParseBytes("INACTIVEPDF_WATCH_LARGE_FILE_RESERVATION_BYTES", 67_108_864),
            Environment.GetEnvironmentVariable("INACTIVEPDF_WATCH_WATERMARK_PROFILE"));
    }

    private static int ParsePositive(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    private static int ParseNonNegative(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
    private static TimeSpan ParseSeconds(string name, int fallback) => TimeSpan.FromSeconds(ParsePositive(name, fallback));
    private static long ParseBytes(string name, long fallback) => long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}

public sealed record WatchFolderRetentionOptions(
    bool Enabled = false,
    TimeSpan SweepInterval = default,
    int MaximumAgeDays = 0,
    long MaximumOutputBytes = 0,
    long MaximumOriginalsBytes = 0,
    long MaximumErrorsBytes = 0,
    long MaximumLogsBytes = 0,
    int MinimumFileAgeSeconds = 300,
    bool DeleteOutputFiles = false,
    bool DeleteOriginalFiles = false,
    bool DeleteErrorFiles = false,
    bool DeleteLogFiles = false)
{
    public TimeSpan EffectiveSweepInterval => SweepInterval == default ? TimeSpan.FromMinutes(5) : SweepInterval;

    public static WatchFolderRetentionOptions FromEnvironment() => new(
        ParseBoolean("INACTIVEPDF_WATCH_RETENTION_ENABLED", false),
        ParseSeconds("INACTIVEPDF_WATCH_RETENTION_SWEEP_INTERVAL_SECONDS", 300),
        ParseNonNegative("INACTIVEPDF_WATCH_RETENTION_MAX_AGE_DAYS", 0),
        ParseBytes("INACTIVEPDF_WATCH_RETENTION_MAX_OUTPUT_BYTES", 0),
        ParseBytes("INACTIVEPDF_WATCH_RETENTION_MAX_ORIGINALS_BYTES", 0),
        ParseBytes("INACTIVEPDF_WATCH_RETENTION_MAX_ERRORS_BYTES", 0),
        ParseBytes("INACTIVEPDF_WATCH_RETENTION_MAX_LOGS_BYTES", 0),
        ParseNonNegative("INACTIVEPDF_WATCH_RETENTION_MIN_FILE_AGE_SECONDS", 300),
        ParseBoolean("INACTIVEPDF_WATCH_RETENTION_DELETE_OUTPUT_FILES", false),
        ParseBoolean("INACTIVEPDF_WATCH_RETENTION_DELETE_ORIGINAL_FILES", false),
        ParseBoolean("INACTIVEPDF_WATCH_RETENTION_DELETE_ERROR_FILES", false),
        ParseBoolean("INACTIVEPDF_WATCH_RETENTION_DELETE_LOG_FILES", false));

    private static bool ParseBoolean(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static int ParseNonNegative(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

    private static TimeSpan ParseSeconds(string name, int fallback) =>
        TimeSpan.FromSeconds(ParsePositive(name, fallback));

    private static int ParsePositive(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static long ParseBytes(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
