using System.Globalization;
using System.Text.Json;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Infrastructure.Configuration;

/// <summary>
/// The single-file deployment configuration for the service. Environment variables remain
/// supported and take precedence, which keeps Windows Service and container deployments safe.
/// </summary>
public sealed class InactivePdfSettings
{
    public ServiceSettings Service { get; set; } = new();
    public PathsSettings Paths { get; set; } = new();
    public ApiSettings Api { get; set; } = new();
    public ResourceSettings Resources { get; set; } = new();
    public WorkerSettings Workers { get; set; } = new();
    public ConcurrencySettings Concurrency { get; set; } = new();
    public WatchFolderSettings WatchFolder { get; set; } = new();
    public ConversionSettings Conversion { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();

    public static InactivePdfSettings Load(string? settingsPath = null)
    {
        var path = ResolveSettingsPath(settingsPath);
        if (path is null) return new InactivePdfSettings();

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<InactivePdfSettings>(json, JsonOptions) ?? new InactivePdfSettings();
            Validate(settings, path);
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"InactivePDF settings file '{path}' is not valid JSON: {exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"InactivePDF settings file '{path}' could not be read: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Loads the file once at process start and projects its values to the existing
    /// environment-based option readers. An explicitly supplied environment value wins.
    /// </summary>
    public static InactivePdfSettings LoadAndApply()
    {
        var settings = Load();
        Apply(settings);
        return settings;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string? ResolveSettingsPath(string? explicitPath = null)
    {
        var configured = explicitPath ?? Environment.GetEnvironmentVariable("INACTIVEPDF_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"The configured InactivePDF settings file was not found: {fullPath}", fullPath);
            return fullPath;
        }

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "InactivePDF.settings.json"),
            Path.Combine(AppContext.BaseDirectory, "InactivePDF.settings.json")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void Apply(InactivePdfSettings settings)
    {
        ApplyValue("INACTIVEPDF_PERFORMANCE_PROFILE", settings.Performance.Profile);
        ApplyValue("INACTIVEPDF_EXECUTION_MODE", settings.Performance.ExecutionMode);
        ApplyValue("INACTIVEPDF_SWARM_WORKERS", settings.Performance.MaximumParallelWorkers);
        ApplyValue("INACTIVEPDF_SWARM_MEMORY_BYTES", settings.Performance.MemoryBudgetBytes);
        ApplyValue("INACTIVEPDF_SWARM_PENDING", settings.Performance.MaximumPending);
        ApplyValue("INACTIVEPDF_SWARM_AGING_SECONDS", settings.Performance.AgingSeconds);
        ApplyValue("ASPNETCORE_URLS", settings.Service.Urls);
        ApplyValue("INACTIVEPDF_SHUTDOWN_TIMEOUT_SECONDS", settings.Service.ShutdownTimeoutSeconds);

        ApplyValue("INACTIVEPDF_DATA_PATH", settings.Paths.DataRoot);
        ApplyValue("INACTIVEPDF_STATE_PATH", settings.Paths.StateRoot);
        ApplyValue("INACTIVEPDF_JOBS_PATH", settings.Paths.JobRoot);
        ApplyValue("INACTIVEPDF_WATCH_ROOT", settings.Paths.WatchRoot);
        ApplyValue("INACTIVEPDF_LIBREOFFICE_PATH", settings.Paths.LibreOfficePath);
        ApplyValue("INACTIVEPDF_CONVERSION_WORKER_PATH", settings.Paths.ConversionWorkerPath);
        ApplyValue("INACTIVEPDF_PDFTOTEXT_PATH", settings.Paths.PdfToTextPath);
        ApplyValue("INACTIVEPDF_PDFTOPPM_PATH", settings.Paths.PdfToPpmPath);

        ApplyValue("INACTIVEPDF_MAX_REQUEST_BYTES", settings.Api.MaximumRequestBytes);
        ApplyValue("INACTIVEPDF_MAX_FILE_BYTES", settings.Api.MaximumFileBytes);
        ApplyValue("INACTIVEPDF_MAX_FILES", settings.Api.MaximumFiles);

        ApplyValue("INACTIVEPDF_MAX_OUTPUT_BYTES", settings.Resources.MaximumOutputBytes);
        ApplyValue("INACTIVEPDF_MAX_IMAGE_PIXELS", settings.Resources.MaximumImagePixels);
        ApplyValue("INACTIVEPDF_MAX_IMAGE_WIDTH", settings.Resources.MaximumImageWidth);
        ApplyValue("INACTIVEPDF_MAX_IMAGE_HEIGHT", settings.Resources.MaximumImageHeight);
        ApplyValue("INACTIVEPDF_MAX_IMAGE_FRAMES", settings.Resources.MaximumImageFrames);
        ApplyValue("INACTIVEPDF_IMAGE_MEMORY_BYTES", settings.Resources.ImageMemoryBytes);
        ApplyValue("INACTIVEPDF_IMAGE_DISK_BYTES", settings.Resources.ImageDiskBytes);
        ApplyValue("INACTIVEPDF_IMAGE_THREAD_COUNT", settings.Resources.ImageThreadCount);
        ApplyValue("INACTIVEPDF_MIN_FREE_DISK_BYTES", settings.Resources.MinimumFreeDiskBytes);
        ApplyValue("INACTIVEPDF_CONVERSION_TIMEOUT_SECONDS", settings.Resources.ConversionTimeoutSeconds);
        ApplyValue("INACTIVEPDF_COPY_BUFFER_BYTES", settings.Resources.CopyBufferBytes);

        ApplyValue("INACTIVEPDF_WORKER_COUNT", settings.Workers.Count);
        ApplyValue("INACTIVEPDF_WORKER_MAX_ATTEMPTS", settings.Workers.MaximumAttempts);
        ApplyValue("INACTIVEPDF_QUEUE_CAPACITY", settings.Workers.QueueCapacity);
        ApplyValue("INACTIVEPDF_CONVERSION_WORKER_TIMEOUT_SECONDS", settings.Workers.TimeoutSeconds);
        ApplyValue("INACTIVEPDF_WORKER_MAX_MEMORY_BYTES", settings.Workers.MaximumMemoryBytes);
        ApplyValue("INACTIVEPDF_JOB_LEASE_SECONDS", settings.Workers.JobLeaseSeconds);
        ApplyValue("INACTIVEPDF_DISPATCHER_POLL_MILLISECONDS", settings.Workers.DispatcherPollMilliseconds);
        ApplyValue("INACTIVEPDF_RETRY_BASE_DELAY_MILLISECONDS", settings.Workers.RetryBaseDelayMilliseconds);

        ApplyValue("INACTIVEPDF_OFFICE_CONCURRENCY", settings.Concurrency.Office);
        ApplyValue("INACTIVEPDF_IMAGE_CONCURRENCY", settings.Concurrency.Image);
        ApplyValue("INACTIVEPDF_PDF_CONCURRENCY", settings.Concurrency.Pdf);
        ApplyValue("INACTIVEPDF_TEXT_CONCURRENCY", settings.Concurrency.Text);

        ApplyValue("INACTIVEPDF_WATCH_CONCURRENCY", settings.WatchFolder.MaximumConcurrentConversions);
        ApplyValue("INACTIVEPDF_WATCH_HEAVY_CONCURRENCY", settings.WatchFolder.MaximumHeavyConversions);
        ApplyValue("INACTIVEPDF_WATCH_MARKUP_CONCURRENCY", settings.WatchFolder.MaximumMarkupConversions);
        ApplyValue("INACTIVEPDF_WATCH_RESOURCE_BUDGET_BYTES", settings.WatchFolder.ResourceBudgetBytes);
        ApplyValue("INACTIVEPDF_WATCH_OFFICE_RESERVATION_BYTES", settings.WatchFolder.OfficeReservationBytes);
        ApplyValue("INACTIVEPDF_WATCH_IMAGE_RESERVATION_BYTES", settings.WatchFolder.ImageReservationBytes);
        ApplyValue("INACTIVEPDF_WATCH_MARKUP_RESERVATION_BYTES", settings.WatchFolder.MarkupReservationBytes);
        ApplyValue("INACTIVEPDF_WATCH_LARGE_FILE_RESERVATION_BYTES", settings.WatchFolder.LargeFileReservationBytes);
        ApplyValue("INACTIVEPDF_WATCH_RETRIES", settings.WatchFolder.MaximumRetries);
        ApplyValue("INACTIVEPDF_WATCH_SCAN_INTERVAL_SECONDS", settings.WatchFolder.ScanIntervalSeconds);
        ApplyValue("INACTIVEPDF_WATCH_FILE_STABILITY_SECONDS", settings.WatchFolder.FileStabilityDelaySeconds);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_ENABLED", settings.WatchFolder.Retention.Enabled);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_SWEEP_INTERVAL_SECONDS", settings.WatchFolder.Retention.SweepIntervalSeconds);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_MAX_AGE_DAYS", settings.WatchFolder.Retention.MaximumAgeDays);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_MAX_OUTPUT_BYTES", settings.WatchFolder.Retention.MaximumOutputBytes);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_MAX_ORIGINALS_BYTES", settings.WatchFolder.Retention.MaximumOriginalsBytes);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_MAX_ERRORS_BYTES", settings.WatchFolder.Retention.MaximumErrorsBytes);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_MAX_LOGS_BYTES", settings.WatchFolder.Retention.MaximumLogsBytes);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_MIN_FILE_AGE_SECONDS", settings.WatchFolder.Retention.MinimumFileAgeSeconds);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_DELETE_OUTPUT_FILES", settings.WatchFolder.Retention.DeleteOutputFiles);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_DELETE_ORIGINAL_FILES", settings.WatchFolder.Retention.DeleteOriginalFiles);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_DELETE_ERROR_FILES", settings.WatchFolder.Retention.DeleteErrorFiles);
        ApplyValue("INACTIVEPDF_WATCH_RETENTION_DELETE_LOG_FILES", settings.WatchFolder.Retention.DeleteLogFiles);
        ApplyValue("INACTIVEPDF_WATCH_PROFILE", settings.WatchFolder.Profile);
        ApplyValue("INACTIVEPDF_WATCH_WATERMARK_PROFILE", settings.WatchFolder.WatermarkProfile);

        ApplyValue("INACTIVEPDF_LIBREOFFICE_TIMEOUT_SECONDS", settings.Paths.LibreOfficeTimeoutSeconds);
        ApplyValue("INACTIVEPDF_LIBREOFFICE_IDLE_TIMEOUT_SECONDS", settings.Paths.LibreOfficeIdleTimeoutSeconds);
        ApplyValue("INACTIVEPDF_LIBREOFFICE_ADDITIONAL_ARGUMENTS", settings.Paths.LibreOfficeAdditionalArguments, allowEmpty: true);
        ApplyValue("INACTIVEPDF_DEFAULT_PROFILE", settings.Conversion.DefaultProfile);
        ApplyValue("INACTIVEPDF_DEFAULT_OPERATION", settings.Conversion.DefaultOperation);
        ApplyProfile("ARCHIVE", settings.Conversion.Profiles.Archive);
        ApplyProfile("COMPACT", settings.Conversion.Profiles.Compact);
        ApplyProfile("COMPATIBILITY", settings.Conversion.Profiles.Compatibility);
    }

    private static void ApplyProfile(string name, PdfProfileSettings profile)
    {
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_PDF_VERSION", profile.PdfVersion);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_PRESERVE_JPEG_DATA", profile.PreserveJpegData);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_DOWNSAMPLE_IMAGES", profile.DownsampleImages);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_MAXIMUM_IMAGE_DPI", profile.MaximumImageDpi);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_JPEG_QUALITY", profile.JpegQuality);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_COMPRESS_CONTENT_STREAMS", profile.CompressContentStreams);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_BEST_COMPRESSION", profile.BestCompression);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_PRESERVE_SOURCE_METADATA", profile.PreserveSourceMetadata);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_FONT_POLICY", profile.FontPolicy);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_ENCRYPTION_POLICY", profile.EncryptionPolicy);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_LARGE_MARKUP_POLICY", profile.LargeMarkupPolicy);
        ApplyValue($"INACTIVEPDF_PROFILE_{name}_STRUCTURAL_VALIDATION", profile.StructuralValidation);
    }

    private static void ApplyValue(string name, string? value, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || Environment.GetEnvironmentVariable(name) is not null) return;
        Environment.SetEnvironmentVariable(name, value);
    }

    private static void ApplyValue(string name, long value) => ApplyValue(name, value.ToString(CultureInfo.InvariantCulture));
    private static void ApplyValue(string name, int value) => ApplyValue(name, value.ToString(CultureInfo.InvariantCulture));
    private static void ApplyValue(string name, bool value) => ApplyValue(name, value ? "true" : "false");

    public static void Validate(InactivePdfSettings settings, string path = "configuration")
    {
        var invalid = new List<string>();
        if (settings.Performance is null) throw new InvalidDataException("Performance must be an object.");
        if (string.IsNullOrWhiteSpace(settings.Performance.Profile) || settings.Performance.Profile.Length > 64) invalid.Add("Performance.Profile requires a name of 1–64 characters");
        try { _ = ConversionExecutionModeParser.Parse(settings.Performance.ExecutionMode); }
        catch (InvalidDataException) { invalid.Add("Performance.ExecutionMode must be either 'Development' or 'Production'"); }
        if (settings.Performance.MaximumParallelWorkers is < 1 or > 256) invalid.Add("Performance.MaximumParallelWorkers must be 1–256");
        if (settings.Performance.MaximumPending is < 1 or > 100000) invalid.Add("Performance.MaximumPending must be 1–100000");
        if (settings.Performance.AgingSeconds is < 1 or > 3600) invalid.Add("Performance.AgingSeconds must be 1–3600");
        if (settings.Performance.MemoryBudgetBytes < 1207959552L) invalid.Add("Performance.MemoryBudgetBytes must admit at least one Office worker (1207959552 bytes)");
        Positive(settings.Service.ShutdownTimeoutSeconds, "Service.ShutdownTimeoutSeconds", invalid);
        Positive(settings.Api.MaximumRequestBytes, "Api.MaximumRequestBytes", invalid);
        Positive(settings.Api.MaximumFileBytes, "Api.MaximumFileBytes", invalid);
        Positive(settings.Api.MaximumFiles, "Api.MaximumFiles", invalid);
        Positive(settings.Resources.MaximumOutputBytes, "Resources.MaximumOutputBytes", invalid);
        Positive(settings.Resources.MaximumImagePixels, "Resources.MaximumImagePixels", invalid);
        Positive(settings.Resources.MaximumImageWidth, "Resources.MaximumImageWidth", invalid);
        Positive(settings.Resources.MaximumImageHeight, "Resources.MaximumImageHeight", invalid);
        Positive(settings.Resources.MaximumImageFrames, "Resources.MaximumImageFrames", invalid);
        Positive(settings.Resources.ImageMemoryBytes, "Resources.ImageMemoryBytes", invalid);
        Positive(settings.Resources.ImageDiskBytes, "Resources.ImageDiskBytes", invalid);
        if (settings.Resources.ImageThreadCount < 0) invalid.Add("Resources.ImageThreadCount must be non-negative");
        Positive(settings.Resources.MinimumFreeDiskBytes, "Resources.MinimumFreeDiskBytes", invalid);
        Positive(settings.Resources.ConversionTimeoutSeconds, "Resources.ConversionTimeoutSeconds", invalid);
        Positive(settings.Resources.CopyBufferBytes, "Resources.CopyBufferBytes", invalid);
        Positive(settings.Workers.Count, "Workers.Count", invalid);
        Positive(settings.Workers.MaximumAttempts, "Workers.MaximumAttempts", invalid);
        Positive(settings.Workers.QueueCapacity, "Workers.QueueCapacity", invalid);
        Positive(settings.Workers.TimeoutSeconds, "Workers.TimeoutSeconds", invalid);
        Positive(settings.Workers.MaximumMemoryBytes, "Workers.MaximumMemoryBytes", invalid);
        if (settings.Workers.JobLeaseSeconds < 30) invalid.Add("Workers.JobLeaseSeconds must be at least 30");
        if (settings.Workers.DispatcherPollMilliseconds < 25) invalid.Add("Workers.DispatcherPollMilliseconds must be at least 25");
        if (settings.Workers.RetryBaseDelayMilliseconds < 25) invalid.Add("Workers.RetryBaseDelayMilliseconds must be at least 25");
        Positive(settings.Concurrency.Office, "Concurrency.Office", invalid);
        Positive(settings.Concurrency.Image, "Concurrency.Image", invalid);
        Positive(settings.Concurrency.Pdf, "Concurrency.Pdf", invalid);
        Positive(settings.Concurrency.Text, "Concurrency.Text", invalid);
        Positive(settings.WatchFolder.MaximumConcurrentConversions, "WatchFolder.MaximumConcurrentConversions", invalid);
        Positive(settings.WatchFolder.MaximumHeavyConversions, "WatchFolder.MaximumHeavyConversions", invalid);
        Positive(settings.WatchFolder.MaximumMarkupConversions, "WatchFolder.MaximumMarkupConversions", invalid);
        Positive(settings.WatchFolder.ResourceBudgetBytes, "WatchFolder.ResourceBudgetBytes", invalid);
        Positive(settings.WatchFolder.OfficeReservationBytes, "WatchFolder.OfficeReservationBytes", invalid);
        Positive(settings.WatchFolder.ImageReservationBytes, "WatchFolder.ImageReservationBytes", invalid);
        Positive(settings.WatchFolder.MarkupReservationBytes, "WatchFolder.MarkupReservationBytes", invalid);
        Positive(settings.WatchFolder.LargeFileReservationBytes, "WatchFolder.LargeFileReservationBytes", invalid);
        if (settings.WatchFolder.MaximumRetries < 0) invalid.Add("WatchFolder.MaximumRetries must be non-negative");
        Positive(settings.WatchFolder.ScanIntervalSeconds, "WatchFolder.ScanIntervalSeconds", invalid);
        Positive(settings.WatchFolder.FileStabilityDelaySeconds, "WatchFolder.FileStabilityDelaySeconds", invalid);
        Positive(settings.WatchFolder.Retention.SweepIntervalSeconds, "WatchFolder.Retention.SweepIntervalSeconds", invalid);
        NonNegative(settings.WatchFolder.Retention.MaximumAgeDays, "WatchFolder.Retention.MaximumAgeDays", invalid);
        NonNegative(settings.WatchFolder.Retention.MaximumOutputBytes, "WatchFolder.Retention.MaximumOutputBytes", invalid);
        NonNegative(settings.WatchFolder.Retention.MaximumOriginalsBytes, "WatchFolder.Retention.MaximumOriginalsBytes", invalid);
        NonNegative(settings.WatchFolder.Retention.MaximumErrorsBytes, "WatchFolder.Retention.MaximumErrorsBytes", invalid);
        NonNegative(settings.WatchFolder.Retention.MaximumLogsBytes, "WatchFolder.Retention.MaximumLogsBytes", invalid);
        NonNegative(settings.WatchFolder.Retention.MinimumFileAgeSeconds, "WatchFolder.Retention.MinimumFileAgeSeconds", invalid);
        Positive(settings.Paths.LibreOfficeTimeoutSeconds, "Paths.LibreOfficeTimeoutSeconds", invalid);
        Positive(settings.Paths.LibreOfficeIdleTimeoutSeconds, "Paths.LibreOfficeIdleTimeoutSeconds", invalid);
        if (string.IsNullOrWhiteSpace(settings.Conversion.DefaultProfile) || settings.Conversion.DefaultProfile.Length > 64)
            invalid.Add("Conversion.DefaultProfile must contain 1 to 64 characters");
        if (!Enum.TryParse(settings.Conversion.DefaultOperation, ignoreCase: true, out InactivePdfOperation _))
            invalid.Add("Conversion.DefaultOperation must be a valid conversion operation");
        ValidateProfile("Conversion.Profiles.Archive", settings.Conversion.Profiles.Archive, invalid);
        ValidateProfile("Conversion.Profiles.Compact", settings.Conversion.Profiles.Compact, invalid);
        ValidateProfile("Conversion.Profiles.Compatibility", settings.Conversion.Profiles.Compatibility, invalid);

        if (invalid.Count > 0)
            throw new InvalidDataException($"InactivePDF settings file '{path}' contains invalid values: {string.Join("; ", invalid)}");
    }

    private static void Positive(long value, string name, List<string> errors)
    {
        if (value <= 0) errors.Add($"{name} must be positive");
    }

    private static void Positive(int value, string name, List<string> errors)
    {
        if (value <= 0) errors.Add($"{name} must be positive");
    }

    private static void NonNegative(long value, string name, List<string> errors)
    {
        if (value < 0) errors.Add($"{name} must be non-negative");
    }

    private static void NonNegative(int value, string name, List<string> errors)
    {
        if (value < 0) errors.Add($"{name} must be non-negative");
    }

    private static void ValidateProfile(string name, PdfProfileSettings? profile, List<string> errors)
    {
        if (profile is null)
        {
            errors.Add($"{name} is required");
            return;
        }

        if (profile.PdfVersion is < 10 or > 20) errors.Add($"{name}.PdfVersion must be between 10 and 20");
        if (profile.MaximumImageDpi < 0) errors.Add($"{name}.MaximumImageDpi must be non-negative");
        if (profile.DownsampleImages && profile.MaximumImageDpi <= 0) errors.Add($"{name}.MaximumImageDpi must be positive when downsampling is enabled");
        if (profile.JpegQuality is < 1 or > 100) errors.Add($"{name}.JpegQuality must be between 1 and 100");
        RequiredPolicy(profile.FontPolicy, $"{name}.FontPolicy", errors);
        RequiredPolicy(profile.EncryptionPolicy, $"{name}.EncryptionPolicy", errors);
        RequiredPolicy(profile.LargeMarkupPolicy, $"{name}.LargeMarkupPolicy", errors);
        if (!string.Equals(profile.LargeMarkupPolicy, "visual-layout", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{name}.LargeMarkupPolicy must be 'visual-layout' because extracted-text HTML/RTF conversion is not implemented as a profile");
    }

    private static void RequiredPolicy(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) errors.Add($"{name} must contain 1 to 128 characters");
    }
}

public sealed class ServiceSettings
{
    public string? Urls { get; set; }
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}

public sealed class PathsSettings
{
    public string? DataRoot { get; set; }
    public string? StateRoot { get; set; }
    public string? JobRoot { get; set; }
    public string? WatchRoot { get; set; }
    public string? LibreOfficePath { get; set; }
    public string? ConversionWorkerPath { get; set; }
    public string? PdfToTextPath { get; set; } = "pdftotext";
    public string? PdfToPpmPath { get; set; } = "pdftoppm";
    public int LibreOfficeTimeoutSeconds { get; set; } = 180;
    public int LibreOfficeIdleTimeoutSeconds { get; set; } = 60;
    public string? LibreOfficeAdditionalArguments { get; set; }
}

public sealed class ApiSettings
{
    public long MaximumRequestBytes { get; set; } = 512L * 1024 * 1024;
    public long MaximumFileBytes { get; set; } = 256L * 1024 * 1024;
    public int MaximumFiles { get; set; } = 50;
}

public sealed class ResourceSettings
{
    public long MaximumOutputBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long MaximumImagePixels { get; set; } = 100_000_000;
    public long MaximumImageWidth { get; set; } = 20_000;
    public long MaximumImageHeight { get; set; } = 20_000;
    public int MaximumImageFrames { get; set; } = 256;
    public long ImageMemoryBytes { get; set; } = 512L * 1024 * 1024;
    public long ImageDiskBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int ImageThreadCount { get; set; }
    public long MinimumFreeDiskBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public int ConversionTimeoutSeconds { get; set; } = 180;
    public int CopyBufferBytes { get; set; } = 64 * 1024;
}

public sealed class WorkerSettings
{
    public int Count { get; set; } = 1;
    public int MaximumAttempts { get; set; } = 2;
    public int QueueCapacity { get; set; } = 256;
    public int TimeoutSeconds { get; set; } = 240;
    public long MaximumMemoryBytes { get; set; } = 1_610_612_736;
    public int JobLeaseSeconds { get; set; } = 300;
    public int DispatcherPollMilliseconds { get; set; } = 250;
    public int RetryBaseDelayMilliseconds { get; set; } = 250;
}

public sealed class PerformanceSettings
{
    public string Profile { get; set; } = "Balanced";
    public string ExecutionMode { get; set; } = "Production";
    public int MaximumParallelWorkers { get; set; } = 2;
    public long MemoryBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaximumPending { get; set; } = 4096;
    public int AgingSeconds { get; set; } = 30;
}

public sealed class ConcurrencySettings
{
    public int Office { get; set; } = 1;
    public int Image { get; set; } = 1;
    public int Pdf { get; set; } = 1;
    public int Text { get; set; } = 2;
}

public sealed class WatchFolderSettings
{
    public string? Root { get; set; }
    public string Profile { get; set; } = "archive";
    public string? WatermarkProfile { get; set; }
    public int MaximumConcurrentConversions { get; set; } = 2;
    public int MaximumHeavyConversions { get; set; } = 2;
    public int MaximumMarkupConversions { get; set; } = 1;
    public long ResourceBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long OfficeReservationBytes { get; set; } = 1_207_959_552;
    public long ImageReservationBytes { get; set; } = 1_073_741_824;
    public long MarkupReservationBytes { get; set; } = 268_435_456;
    public long LargeFileReservationBytes { get; set; } = 67_108_864;
    public int MaximumRetries { get; set; } = 1;
    public int ScanIntervalSeconds { get; set; } = 2;
    public int FileStabilityDelaySeconds { get; set; } = 2;
    public RetentionSettings Retention { get; set; } = new();
}

public sealed class RetentionSettings
{
    public bool Enabled { get; set; }
    public int SweepIntervalSeconds { get; set; } = 300;
    public int MaximumAgeDays { get; set; }
    public long MaximumOutputBytes { get; set; }
    public long MaximumOriginalsBytes { get; set; }
    public long MaximumErrorsBytes { get; set; }
    public long MaximumLogsBytes { get; set; }
    public int MinimumFileAgeSeconds { get; set; } = 300;
    public bool DeleteOutputFiles { get; set; }
    public bool DeleteOriginalFiles { get; set; }
    public bool DeleteErrorFiles { get; set; }
    public bool DeleteLogFiles { get; set; }
}

public sealed class ConversionSettings
{
    public string DefaultProfile { get; set; } = "archive";
    public string DefaultOperation { get; set; } = "ConvertFile";
    public PdfProfilesSettings Profiles { get; set; } = new();
}

public sealed class PdfProfilesSettings
{
    public PdfProfileSettings Archive { get; set; } = PdfProfileSettings.ArchiveDefaults();
    public PdfProfileSettings Compact { get; set; } = PdfProfileSettings.CompactDefaults();
    public PdfProfileSettings Compatibility { get; set; } = PdfProfileSettings.CompatibilityDefaults();
}

public sealed class PdfProfileSettings
{
    public int PdfVersion { get; set; }
    public bool PreserveJpegData { get; set; }
    public bool DownsampleImages { get; set; }
    public int MaximumImageDpi { get; set; }
    public int JpegQuality { get; set; }
    public bool CompressContentStreams { get; set; }
    public bool BestCompression { get; set; }
    public bool PreserveSourceMetadata { get; set; }
    public string FontPolicy { get; set; } = string.Empty;
    public string EncryptionPolicy { get; set; } = string.Empty;
    public string LargeMarkupPolicy { get; set; } = string.Empty;
    public bool StructuralValidation { get; set; }

    public static PdfProfileSettings ArchiveDefaults() => new()
    {
        PdfVersion = 17,
        PreserveJpegData = true,
        DownsampleImages = false,
        MaximumImageDpi = 0,
        JpegQuality = 100,
        CompressContentStreams = true,
        BestCompression = false,
        PreserveSourceMetadata = true,
        FontPolicy = "system-fonts-with-fallback",
        EncryptionPolicy = "preserve-unencrypted-or-reject-password-protected",
        LargeMarkupPolicy = "visual-layout",
        StructuralValidation = true
    };

    public static PdfProfileSettings CompactDefaults() => new()
    {
        PdfVersion = 17,
        PreserveJpegData = false,
        DownsampleImages = true,
        MaximumImageDpi = 150,
        JpegQuality = 75,
        CompressContentStreams = true,
        BestCompression = true,
        PreserveSourceMetadata = false,
        FontPolicy = "system-fonts-with-fallback",
        EncryptionPolicy = "none",
        LargeMarkupPolicy = "visual-layout",
        StructuralValidation = true
    };

    public static PdfProfileSettings CompatibilityDefaults() => new()
    {
        PdfVersion = 14,
        PreserveJpegData = true,
        DownsampleImages = false,
        MaximumImageDpi = 0,
        JpegQuality = 100,
        CompressContentStreams = true,
        BestCompression = false,
        PreserveSourceMetadata = false,
        FontPolicy = "system-fonts-with-fallback",
        EncryptionPolicy = "none",
        LargeMarkupPolicy = "visual-layout",
        StructuralValidation = true
    };
}

internal enum InactivePdfOperation
{
    ConvertFile,
    ConvertFiles,
    ConvertAndMerge,
    CreateTextPdf
}
