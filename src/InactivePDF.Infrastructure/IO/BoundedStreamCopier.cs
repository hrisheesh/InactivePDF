using System.Buffers;
using InactivePDF.Domain.Contracts;

namespace InactivePDF.Infrastructure.IO;

public sealed class BoundedStreamCopier : IBoundedStreamCopier
{
    public async Task<long> CopyAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        int bufferSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSizeBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(bufferSizeBytes);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, bufferSizeBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new IOException($"The stream exceeds the maximum allowed size of {maximumBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
