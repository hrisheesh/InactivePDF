using InactivePDF.Infrastructure.IO;

namespace InactivePDF.UnitTests.IO;

public sealed class BoundedStreamCopierTests
{
    [Fact]
    public async Task CopyRespectsMaximumBytes()
    {
        var source = new MemoryStream(new byte[10]);
        await using var destination = new MemoryStream();
        var copier = new BoundedStreamCopier();

        await Assert.ThrowsAsync<IOException>(() => copier.CopyAsync(source, destination, 5, 4));
    }

    [Fact]
    public async Task CopyUsesCancellation()
    {
        var source = new MemoryStream(new byte[10]);
        await using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var copier = new BoundedStreamCopier();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copier.CopyAsync(source, destination, 100, 4, cancellation.Token));
    }
}
