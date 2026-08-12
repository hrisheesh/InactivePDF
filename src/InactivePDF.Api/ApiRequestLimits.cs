namespace InactivePDF.Api;

public sealed record ApiRequestLimits(
    long MaximumRequestBytes,
    long MaximumFileBytes,
    int MaximumFiles)
{
    public static ApiRequestLimits FromEnvironment() => new(
        ParseBytes("INACTIVEPDF_MAX_REQUEST_BYTES", 512L * 1024 * 1024),
        ParseBytes("INACTIVEPDF_MAX_FILE_BYTES", 256L * 1024 * 1024),
        ParsePositive("INACTIVEPDF_MAX_FILES", 50));

    private static long ParseBytes(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int ParsePositive(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
