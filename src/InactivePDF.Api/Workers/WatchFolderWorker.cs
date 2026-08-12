using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Watch;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Models;
using InactivePDF.Domain.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace InactivePDF.Api.Workers;

public sealed class WatchFolderWorker(
    WatchFolderOptions options,
    IsolatedConversionWorker converter,
    IDiskSpaceGuard diskSpaceGuard,
    ResourcePolicy resourcePolicy,
    ILogger<WatchFolderWorker> logger) : BackgroundService
{
    private readonly StableFileDetector _stableFiles = new(options.EffectiveFileStabilityDelay);
    private readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inflightGate = new();
    private readonly HashSet<Task> _processingTasks = new();
    private readonly object _processingTasksGate = new();
    private readonly SemaphoreSlim _heavyConversionGate = new(Math.Max(1, options.MaximumHeavyConversions), Math.Max(1, options.MaximumHeavyConversions));
    private readonly SemaphoreSlim _markupConversionGate = new(Math.Max(1, options.MaximumMarkupConversions), Math.Max(1, options.MaximumMarkupConversions));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WatchFolderLayout.Ensure(options);
        using var fileLogger = new WatchFolderLogger(options.LogPath);
        await fileLogger.WriteServiceAsync("service-started", $"root={options.RootPath}", CancellationToken.None).ConfigureAwait(false);
        await RecoverProcessingFilesAsync(fileLogger).ConfigureAwait(false);
        var monitor = new ServiceResourceMonitor(fileLogger);
        var monitorTask = monitor.RunAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var path in Directory.EnumerateFiles(options.InputPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !IsHidden(path))
                    .OrderBy(GetPriority)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(path);
                    if (IsInFlight(path) || !_stableFiles.IsStable(path) || !TryClaim(path, out var claimedPath, out var originalName)) continue;
                    StartProcessingTask(claimedPath, originalName, fileLogger, stoppingToken);
                }

                await Task.Delay(options.EffectiveScanInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try { await monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await DrainProcessingTasksAsync().ConfigureAwait(false);
            await fileLogger.WriteServiceAsync("service-stopped", "watch folder worker stopped", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _heavyConversionGate.Dispose();
        _markupConversionGate.Dispose();
        base.Dispose();
    }

    private async Task ProcessAndForgetAsync(string path, string originalName, WatchFolderLogger fileLogger, CancellationToken cancellationToken)
    {
        try { await ProcessAsync(path, originalName, fileLogger, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { TryMove(path, options.InputPath, originalName); }
        catch (Exception exception)
        {
            TryMove(path, options.ErrorsPath, originalName);
            WatchLog.Failed(logger, exception, originalName);
            await fileLogger.WriteAsync("error", originalName, false, 1, exception, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        finally { lock (_inflightGate) _inflight.Remove(path); }
    }

    private void StartProcessingTask(string path, string originalName, WatchFolderLogger fileLogger, CancellationToken cancellationToken)
    {
        var task = ProcessAndForgetAsync(path, originalName, fileLogger, cancellationToken);
        lock (_processingTasksGate) _processingTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_processingTasksGate) _processingTasks.Remove(completed);
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainProcessingTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_processingTasksGate) tasks = _processingTasks.ToArray();
            if (tasks.Length == 0) return;
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception exception) { WatchLog.Failed(logger, exception, "service-shutdown"); }
        }
    }

    private async Task ProcessAsync(string path, string originalName, WatchFolderLogger fileLogger, CancellationToken cancellationToken)
    {
        var fileName = originalName;
        var inputBytes = new FileInfo(path).Length;
        if (inputBytes > resourcePolicy.MaximumInputBytes)
            throw new IOException($"The input file exceeds the maximum allowed size of {resourcePolicy.MaximumInputBytes} bytes.");

        diskSpaceGuard.EnsureAvailable(options.RootPath, resourcePolicy.MinimumFreeDiskBytes);
        SemaphoreSlim? acquiredGate = null;
        var gateWaitMs = 0L;
        IsolatedWorkerMetrics? workerMetrics = null;
        try
        {
            var gate = SelectConversionGate(fileName, inputBytes);
            if (gate is not null)
            {
                var gateStarted = Stopwatch.GetTimestamp();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateWaitMs = (long)Stopwatch.GetElapsedTime(gateStarted).TotalMilliseconds;
                acquiredGate = gate;
            }

            for (var attempt = 1; attempt <= options.MaximumRetries + 1; attempt++)
            {
                var started = Stopwatch.GetTimestamp();
                var cpuBeforeMs = (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
                using var samplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var sampler = new ResourceSampler(fileLogger);
                await fileLogger.WriteResourceAsync(fileName, "start", ResourceSnapshot.Capture(), cancellationToken).ConfigureAwait(false);
                var samplerTask = sampler.SampleAsync(fileName, samplerCancellation.Token);
                try
                {
                    var output = CreateOutputPath(fileName);
                    workerMetrics = await converter.ConvertAsync(
                        new ConversionWorkerRequest(
                            ConversionOperation.ConvertFile,
                            output,
                            [new ConversionWorkerInput(path, fileName)]),
                        cancellationToken).ConfigureAwait(false);
                    if (!TryMove(path, options.OriginalsPath, fileName))
                        throw new IOException($"Conversion completed but the source could not be moved to the originals folder: {options.OriginalsPath}");
                    var outputBytes = new FileInfo(output).Length;
                    var resource = ResourceSnapshot.Capture().WithConversionWorker(workerMetrics);
                    await fileLogger.WriteAsync("converted", fileName, true, attempt, durationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputBytes: inputBytes, outputBytes: outputBytes, workingSetBytes: resource.WorkingSetBytes, cpuMs: Math.Max(0, resource.CpuMilliseconds - cpuBeforeMs), resource: resource, gateWaitMs: gateWaitMs, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (attempt <= options.MaximumRetries && !cancellationToken.IsCancellationRequested && IsRetryable(exception))
                {
                    var resource = ResourceSnapshot.Capture();
                    await fileLogger.WriteAsync("retry", fileName, false, attempt, exception, durationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputBytes: inputBytes, workingSetBytes: resource.WorkingSetBytes, cpuMs: Math.Max(0, resource.CpuMilliseconds - cpuBeforeMs), resource: resource, gateWaitMs: gateWaitMs, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << Math.Min(attempt - 1, 6))), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    TryMove(path, options.ErrorsPath, fileName);
                    var resource = ResourceSnapshot.Capture();
                    await fileLogger.WriteAsync("error", fileName, false, attempt, exception, durationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputBytes: inputBytes, workingSetBytes: resource.WorkingSetBytes, cpuMs: Math.Max(0, resource.CpuMilliseconds - cpuBeforeMs), resource: resource, gateWaitMs: gateWaitMs, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    WatchLog.Failed(logger, exception, fileName);
                    return;
                }
                finally
                {
                    samplerCancellation.Cancel();
                    try { await samplerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    await fileLogger.WriteResourceAsync(fileName, "end", ResourceSnapshot.Capture().WithConversionWorker(workerMetrics), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            acquiredGate?.Release();
        }
    }

    private bool TryClaim(string inputPath, out string claimedPath, out string originalName)
    {
        originalName = Path.GetFileName(inputPath);
        claimedPath = Path.Combine(options.ProcessingPath, $"{Guid.NewGuid():N}-{originalName}");
        lock (_inflightGate) if (_inflight.Count >= options.MaximumConcurrentConversions) return false;
        try { File.Move(inputPath, claimedPath); lock (_inflightGate) _inflight.Add(claimedPath); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool IsInFlight(string path) { lock (_inflightGate) return _inflight.Contains(path); }

    private static bool IsHidden(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Length > 0 && fileName[0] == '.';
    }

    private static bool IsImage(string path) => SupportedFormatCatalog.IsImage(Path.GetExtension(path));

    private SemaphoreSlim? SelectConversionGate(string path, long inputBytes)
    {
        var extension = Path.GetExtension(path);
        if (extension is ".html" or ".htm" or ".rtf") return _markupConversionGate;
        if (IsImage(path)) return _heavyConversionGate;
        if (SupportedFormatCatalog.TryGet(extension, out var format) && format.Route == ConversionFormatRoute.LibreOffice) return _heavyConversionGate;
        return inputBytes >= 1L * 1024 * 1024 ? _heavyConversionGate : null;
    }

    private static int GetPriority(string path)
    {
        if (!SupportedFormatCatalog.TryGet(Path.GetExtension(path), out var format)) return 4;
        return format.Category switch
        {
            ConversionFormatCategory.Html => 0,
            ConversionFormatCategory.PlainText or ConversionFormatCategory.Pdf => 1,
            ConversionFormatCategory.Image => 2,
            _ => 3
        };
    }

    private async Task RecoverProcessingFilesAsync(WatchFolderLogger fileLogger)
    {
        foreach (var path in Directory.EnumerateFiles(options.ProcessingPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Length == 0 || fileName[0] == '.') continue;
            var originalName = GetOriginalName(fileName);
            var inputBytes = new FileInfo(path).Length;
            if (TryMove(path, options.InputPath, originalName))
            {
                await fileLogger.WriteAsync("recovered", originalName, true, 0, inputBytes: inputBytes, resource: ResourceSnapshot.Capture(), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private string CreateOutputPath(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var path = Path.Combine(options.OutputPath, baseName + ".pdf");
        return File.Exists(path) ? Path.Combine(options.OutputPath, $"{baseName}-{Guid.NewGuid():N}.pdf") : path;
    }
    private static bool TryMove(string path, string destinationDirectory, string originalName)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            File.Move(path, GetAvailablePath(destinationDirectory, originalName), overwrite: false);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string GetOriginalName(string processingName) =>
        processingName.Length > 33 && processingName[32] == '-' && processingName[..32].All(Uri.IsHexDigit)
            ? processingName[33..]
            : processingName;

    private static string GetAvailablePath(string directory, string fileName)
    {
        var first = Path.Combine(directory, fileName);
        if (!File.Exists(first)) return first;
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem}duplicate{suffix:D2}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static bool IsRetryable(Exception exception) => exception is IOException;
}

internal static partial class WatchLog
{
    [LoggerMessage(LogLevel.Error, "Watch-folder conversion failed for {FileName}")]
    public static partial void Failed(ILogger logger, Exception exception, string fileName);
}
