using InactivePDF.Infrastructure.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InactivePDF.Api.Workers;

public sealed class WatchFolderRetentionWorker(
    WatchFolderOptions folders,
    WatchFolderRetentionOptions options,
    ILogger<WatchFolderRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        WatchFolderLayout.Ensure(folders);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = new WatchFolderRetentionService(folders, options).Sweep(DateTimeOffset.UtcNow);
                if (result.DeletedFiles > 0)
                    WatchFolderRetentionLog.Reclaimed(logger, result.DeletedFiles, result.DeletedBytes);
            }
            catch (Exception)
            {
                WatchFolderRetentionLog.Failed(logger);
            }

            await Task.Delay(options.EffectiveSweepInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class WatchFolderRetentionLog
{
    [LoggerMessage(LogLevel.Information, "Retention sweep deleted {DeletedFiles} files and reclaimed {DeletedBytes} bytes.")]
    public static partial void Reclaimed(ILogger logger, int deletedFiles, long deletedBytes);

    [LoggerMessage(LogLevel.Error, "Retention sweep could not complete for the watch folder.")]
    public static partial void Failed(ILogger logger);
}
