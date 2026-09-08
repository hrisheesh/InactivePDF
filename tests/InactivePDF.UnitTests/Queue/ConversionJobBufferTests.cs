using InactivePDF.Application;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Models;

namespace InactivePDF.UnitTests.Queue;

public sealed class ConversionJobBufferTests
{
    [Fact]
    public async Task EngineLanesInterleaveAndPreserveTheirOwnOrder()
    {
        using var buffer = new ConversionJobBuffer(10);
        var office1 = CreateItem() with { Inputs = [new StoredInput("first.docx", "first.docx", 1)] };
        var office2 = CreateItem() with { Inputs = [new StoredInput("second.docx", "second.docx", 1)] };
        var text1 = CreateItem();var text2 = CreateItem();
        Assert.True(buffer.TryEnqueue(office1));Assert.True(buffer.TryEnqueue(office2));
        Assert.True(buffer.TryEnqueue(text1));Assert.True(buffer.TryEnqueue(text2));
        Assert.Equal(text1, await buffer.DequeueAsync());Assert.Equal(office1, await buffer.DequeueAsync());
        Assert.True(buffer.TryDequeue(out var third));Assert.Equal(text2, third);
        Assert.Equal(office2, await buffer.DequeueAsync());Assert.Equal(0, buffer.Count);
        using var cancel = new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await buffer.DequeueAsync(cancel.Token));
        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public async Task ConcurrentConsumersAccountForEveryBufferedItemOnce()
    {
        using var buffer = new ConversionJobBuffer(2000);
        var ids = new System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>();
        for(var i=0;i<2000;i++)Assert.True(buffer.TryEnqueue(CreateItem()));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_=>Task.Run(()=>
        {
            while(buffer.TryDequeue(out var item))Assert.True(ids.TryAdd(item!.Job.Id, true));
        })));
        Assert.Equal(2000, ids.Count);Assert.Equal(0, buffer.Count);
    }

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
