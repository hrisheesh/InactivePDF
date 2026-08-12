namespace InactivePDF.Infrastructure.Watch;

public sealed class ServiceResourceMonitor(WatchFolderLogger logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ResourceSnapshot? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = ResourceSnapshot.Capture();
            if (snapshot.IsMateriallyDifferent(previous, TimeSpan.FromMinutes(1)))
            {
                await logger.WriteResourceAsync("service", "heartbeat", snapshot, cancellationToken).ConfigureAwait(false);
                previous = snapshot;
            }
        }
    }
}
