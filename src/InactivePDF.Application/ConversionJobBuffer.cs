using InactivePDF.Application.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Application;

public sealed class ConversionJobBuffer(int capacity) : Abstractions.IConversionJobBuffer, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<ConversionWorkItem>[] _lanes = [new(), new(), new(), new()];
    private readonly SemaphoreSlim _available = new(0);
    private int _count, _nextLane;

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count { get { lock (_gate) return _count; } }
    public bool TryEnqueue(ConversionWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_count >= Capacity) return false;
            var lane = item.Inputs.Select(input => SupportedFormatCatalog.TryGet(Path.GetExtension(input.FileName), out var f) ? f.Route switch
            { ConversionFormatRoute.TextGenerator => 0, ConversionFormatRoute.PdfPassThrough => 1, ConversionFormatRoute.Image => 2, _ => 3 } : 3).DefaultIfEmpty(0).Max();
            _lanes[lane].Enqueue(item);_count++;_available.Release();return true;
        }
    }
    public bool TryDequeue(out ConversionWorkItem? item)
    {
        if (!_available.Wait(0)) { item = null;return false; }
        item = Take();return true;
    }
    public async ValueTask<ConversionWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Take();
    }
    private ConversionWorkItem Take()
    {
        lock (_gate)
        {
            // Round-robin engine lanes preserve FIFO within each lane and prevent a large
            // Office burst from occupying the whole dispatch order ahead of light work.
            for (var i = 0; i < _lanes.Length; i++)
            {
                var lane = _nextLane;_nextLane = (_nextLane + 1) % _lanes.Length;
                if (_lanes[lane].TryDequeue(out var item)) { _count--;return item; }
            }
            throw new InvalidOperationException("Dispatch buffer permit has no matching work item.");
        }
    }
    public void Dispose() => _available.Dispose();
}
