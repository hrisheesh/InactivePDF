namespace InactivePDF.Domain.Models;

public sealed record ResourcePolicy(
    long MaximumInputBytes,
    long MaximumOutputBytes,
    long MinimumFreeDiskBytes,
    TimeSpan MaximumDuration,
    int BufferSizeBytes = 64 * 1024,
    long MaximumImagePixels = 50_000_000)
{
    public static ResourcePolicy Default { get; } = new(
        MaximumInputBytes: 256L * 1024 * 1024,
        MaximumOutputBytes: 2L * 1024 * 1024 * 1024,
        MinimumFreeDiskBytes: 10L * 1024 * 1024 * 1024,
        MaximumDuration: TimeSpan.FromMinutes(3),
        MaximumImagePixels: 50_000_000);

    public static ResourcePolicy FromEnvironment() => new(
        ParseBytes("INACTIVEPDF_MAX_FILE_BYTES", Default.MaximumInputBytes),
        ParseBytes("INACTIVEPDF_MAX_OUTPUT_BYTES", Default.MaximumOutputBytes),
        ParseBytes("INACTIVEPDF_MIN_FREE_DISK_BYTES", Default.MinimumFreeDiskBytes),
        TimeSpan.FromSeconds(ParsePositive("INACTIVEPDF_CONVERSION_TIMEOUT_SECONDS", (int)Default.MaximumDuration.TotalSeconds)),
        ParsePositive("INACTIVEPDF_COPY_BUFFER_BYTES", Default.BufferSizeBytes),
        ParsePositiveLong("INACTIVEPDF_MAX_IMAGE_PIXELS", Default.MaximumImagePixels));

    private static long ParseBytes(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int ParsePositive(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static long ParsePositiveLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
