namespace InactivePDF.Infrastructure.Watch;

public sealed class ResourceSampler(WatchFolderLogger logger)
{
    public async Task SampleAsync(string path, CancellationToken cancellationToken)
    {
        ResourceSnapshot? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = ResourceSnapshot.Capture();
            if (snapshot.IsMateriallyDifferent(previous, TimeSpan.FromSeconds(15)))
            {
                await logger.WriteResourceAsync(path, "sample", snapshot, cancellationToken).ConfigureAwait(false);
                previous = snapshot;
            }
        }
    }
}
