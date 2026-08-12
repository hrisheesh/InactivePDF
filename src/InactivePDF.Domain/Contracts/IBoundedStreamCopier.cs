namespace InactivePDF.Domain.Contracts;

public interface IBoundedStreamCopier
{
    Task<long> CopyAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        int bufferSizeBytes,
        CancellationToken cancellationToken = default);
}
