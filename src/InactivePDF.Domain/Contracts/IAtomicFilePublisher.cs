namespace InactivePDF.Domain.Contracts;

public interface IAtomicFilePublisher
{
    Task<long> PublishAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken = default);
}
