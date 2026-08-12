using System.Threading.Channels;
using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;

namespace InactivePDF.Application;

public sealed class ConversionJobBuffer(int capacity) : Abstractions.IConversionJobBuffer
{
    private readonly Channel<ConversionWorkItem> _channel = Channel.CreateBounded<ConversionWorkItem>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count => _channel.Reader.Count;
    public bool TryEnqueue(ConversionWorkItem item) => _channel.Writer.TryWrite(item);
    public bool TryDequeue(out ConversionWorkItem? item) => _channel.Reader.TryRead(out item);
    public ValueTask<ConversionWorkItem> DequeueAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAsync(cancellationToken);
}
