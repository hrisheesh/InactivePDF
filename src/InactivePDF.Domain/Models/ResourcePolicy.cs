namespace InactivePDF.Domain.Models;

public sealed record ResourcePolicy(
    long MaximumInputBytes,
    long MaximumOutputBytes,
    long MinimumFreeDiskBytes,
    TimeSpan MaximumDuration,
    int BufferSizeBytes = 64 * 1024,
    long MaximumImagePixels = 100_000_000,
    long MaximumImageWidth = 20_000,
    long MaximumImageHeight = 20_000,
    int MaximumImageFrames = 256,
    long ImageMemoryBytes = 512L * 1024 * 1024,
    long ImageDiskBytes = 2L * 1024 * 1024 * 1024,
    int ImageThreadCount = 0)
{
    public static ResourcePolicy Default { get; } = new(
        MaximumInputBytes: 256L * 1024 * 1024,
        MaximumOutputBytes: 2L * 1024 * 1024 * 1024,
        MinimumFreeDiskBytes: 10L * 1024 * 1024 * 1024,
        MaximumDuration: TimeSpan.FromMinutes(3),
        MaximumImagePixels: 100_000_000,
        MaximumImageWidth: 20_000,
        MaximumImageHeight: 20_000,
        MaximumImageFrames: 256,
        ImageMemoryBytes: 512L * 1024 * 1024,
        ImageDiskBytes: 2L * 1024 * 1024 * 1024);

    public static ResourcePolicy FromEnvironment() => new(
        ParseBytes("INACTIVEPDF_MAX_FILE_BYTES", Default.MaximumInputBytes),
        ParseBytes("INACTIVEPDF_MAX_OUTPUT_BYTES", Default.MaximumOutputBytes),
        ParseBytes("INACTIVEPDF_MIN_FREE_DISK_BYTES", Default.MinimumFreeDiskBytes),
        TimeSpan.FromSeconds(ParsePositive("INACTIVEPDF_CONVERSION_TIMEOUT_SECONDS", (int)Default.MaximumDuration.TotalSeconds)),
        ParsePositive("INACTIVEPDF_COPY_BUFFER_BYTES", Default.BufferSizeBytes),
        ParsePositiveLong("INACTIVEPDF_MAX_IMAGE_PIXELS", Default.MaximumImagePixels),
        ParsePositiveLong("INACTIVEPDF_MAX_IMAGE_WIDTH", Default.MaximumImageWidth),
        ParsePositiveLong("INACTIVEPDF_MAX_IMAGE_HEIGHT", Default.MaximumImageHeight),
        ParsePositive("INACTIVEPDF_MAX_IMAGE_FRAMES", Default.MaximumImageFrames),
        ParsePositiveLong("INACTIVEPDF_IMAGE_MEMORY_BYTES", Default.ImageMemoryBytes),
        ParsePositiveLong("INACTIVEPDF_IMAGE_DISK_BYTES", Default.ImageDiskBytes),
        ParseNonNegative("INACTIVEPDF_IMAGE_THREAD_COUNT", Default.ImageThreadCount));

    private static long ParseBytes(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int ParsePositive(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int ParseNonNegative(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

    private static long ParsePositiveLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
