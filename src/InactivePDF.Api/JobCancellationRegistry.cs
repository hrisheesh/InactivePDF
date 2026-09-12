using System.Collections.Concurrent;

namespace InactivePDF.Api;

public sealed class JobCancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = new();

    public CancellationToken Register(Guid jobId)
    {
        var source = new CancellationTokenSource();
        if (!_active.TryAdd(jobId, source))
        {
            source.Dispose();
            return _active[jobId].Token;
        }

        if (_cancelled.ContainsKey(jobId)) source.Cancel();
        return source.Token;
    }

    public bool Cancel(Guid jobId)
    {
        _cancelled[jobId] = 0;
        return _active.TryGetValue(jobId, out var source) && TryCancel(source);
    }

    public void Reset(Guid jobId) => _cancelled.TryRemove(jobId, out _);

    public void Complete(Guid jobId)
    {
        if (_active.TryRemove(jobId, out var source)) source.Dispose();
        _cancelled.TryRemove(jobId, out _);
    }

    public void Dispose()
    {
        foreach (var pair in _active) pair.Value.Dispose();
        _active.Clear();
        _cancelled.Clear();
    }

    private static bool TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }
}
