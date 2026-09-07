namespace InactivePDF.Infrastructure.Watch;

public sealed class StableFileDetector(TimeSpan stabilityDelay)
{
    private readonly Dictionary<string, Observation> _observed = new(StringComparer.OrdinalIgnoreCase);

    public bool IsStable(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { _observed.Remove(path); return false; }
        var current = new FileStamp(info.Length, info.LastWriteTimeUtc);
        if (!_observed.TryGetValue(path, out var previous) || previous.Stamp != current)
        {
            _observed[path] = new Observation(current, DateTimeOffset.UtcNow);
            return false;
        }

        return DateTime.UtcNow - info.LastWriteTimeUtc >= stabilityDelay && CanOpenForRead(path);
    }

    public void Forget(string path) => _observed.Remove(path);

    public DateTimeOffset GetFirstSeenUtc(string path) =>
        _observed.TryGetValue(path, out var observation) ? observation.FirstSeenUtc : DateTimeOffset.UtcNow;
    private static bool CanOpenForRead(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return stream.Length >= 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteUtc);
    private readonly record struct Observation(FileStamp Stamp, DateTimeOffset FirstSeenUtc);
}
