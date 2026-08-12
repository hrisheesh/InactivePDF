using InactivePDF.Domain.Contracts;

namespace InactivePDF.Infrastructure.IO;

public sealed class AtomicFilePublisher(IBoundedStreamCopier copier) : IAtomicFilePublisher
{
    public async Task<long> PublishAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullDestination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new ArgumentException("The destination must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.partial");
        try
        {
            long bytes;
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                bytes = await copier.CopyAsync(source, temporary, maximumBytes, 64 * 1024, cancellationToken).ConfigureAwait(false);
                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullDestination, overwrite: true);
            return bytes;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
