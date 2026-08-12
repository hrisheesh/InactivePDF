using System.Globalization;
using System.Text.Json;

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

    private static string? ResolveSettingsPath(string? explicitPath)
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
        ApplyValue("INACTIVEPDF_WATCH_RETRIES", settings.WatchFolder.MaximumRetries);
        ApplyValue("INACTIVEPDF_WATCH_SCAN_INTERVAL_SECONDS", settings.WatchFolder.ScanIntervalSeconds);
        ApplyValue("INACTIVEPDF_WATCH_FILE_STABILITY_SECONDS", settings.WatchFolder.FileStabilityDelaySeconds);

        ApplyValue("INACTIVEPDF_LIBREOFFICE_TIMEOUT_SECONDS", settings.Paths.LibreOfficeTimeoutSeconds);
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

    private static void Validate(InactivePdfSettings settings, string path)
    {
        var invalid = new List<string>();
        Positive(settings.Service.ShutdownTimeoutSeconds, "Service.ShutdownTimeoutSeconds", invalid);
        Positive(settings.Api.MaximumRequestBytes, "Api.MaximumRequestBytes", invalid);
        Positive(settings.Api.MaximumFileBytes, "Api.MaximumFileBytes", invalid);
        Positive(settings.Api.MaximumFiles, "Api.MaximumFiles", invalid);
        Positive(settings.Resources.MaximumOutputBytes, "Resources.MaximumOutputBytes", invalid);
        Positive(settings.Resources.MaximumImagePixels, "Resources.MaximumImagePixels", invalid);
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
        if (settings.WatchFolder.MaximumRetries < 0) invalid.Add("WatchFolder.MaximumRetries must be non-negative");
        Positive(settings.WatchFolder.ScanIntervalSeconds, "WatchFolder.ScanIntervalSeconds", invalid);
        Positive(settings.WatchFolder.FileStabilityDelaySeconds, "WatchFolder.FileStabilityDelaySeconds", invalid);
        Positive(settings.Paths.LibreOfficeTimeoutSeconds, "Paths.LibreOfficeTimeoutSeconds", invalid);
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
    public long MaximumImagePixels { get; set; } = 50_000_000;
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
    public int MaximumConcurrentConversions { get; set; } = 2;
    public int MaximumHeavyConversions { get; set; } = 1;
    public int MaximumMarkupConversions { get; set; } = 1;
    public int MaximumRetries { get; set; } = 1;
    public int ScanIntervalSeconds { get; set; } = 2;
    public int FileStabilityDelaySeconds { get; set; } = 2;
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
