using InactivePDF.Application;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Models;

namespace InactivePDF.UnitTests.Queue;

public sealed class ConversionJobBufferTests
{
    [Fact]
    public async Task BufferRejectsItemsWhenCapacityIsReached()
    {
        var buffer = new ConversionJobBuffer(1);
        var first = CreateItem();
        var second = CreateItem();

        Assert.True(buffer.TryEnqueue(first));
        Assert.False(buffer.TryEnqueue(second));
        Assert.Equal(first, await buffer.DequeueAsync());
    }

    private static ConversionWorkItem CreateItem()
    {
        var job = new ConversionJob(Guid.NewGuid(), "test", ConversionOperation.CreateTextPdf, ConversionJobState.Accepted, DateTimeOffset.UtcNow);
        var request = new ConversionRequest("test", ConversionOperation.CreateTextPdf, Array.Empty<DocumentInput>(), new ConversionOptions());
        return new ConversionWorkItem(job, request, Array.Empty<StoredInput>(), Path.GetTempPath());
    }
}
