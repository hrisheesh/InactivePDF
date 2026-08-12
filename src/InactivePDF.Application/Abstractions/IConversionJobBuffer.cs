using InactivePDF.Application.Models;

namespace InactivePDF.Application.Abstractions;

public interface IConversionJobBuffer
{
    int Capacity { get; }
    int Count { get; }
    bool TryEnqueue(ConversionWorkItem item);
    bool TryDequeue(out ConversionWorkItem? item);
    ValueTask<ConversionWorkItem> DequeueAsync(CancellationToken cancellationToken = default);
}
