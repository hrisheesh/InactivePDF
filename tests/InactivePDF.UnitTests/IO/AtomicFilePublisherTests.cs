using InactivePDF.Infrastructure.IO;

namespace InactivePDF.UnitTests.IO;

public sealed class AtomicFilePublisherTests
{
    [Fact]
    public async Task PublishCreatesCompleteDestinationAndNoPartialFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "output.pdf");
            await using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            var publisher = new AtomicFilePublisher(new BoundedStreamCopier());

            var bytes = await publisher.PublishAsync(source, destination, 100);

            Assert.Equal(4, bytes);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(root, "*.partial"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
