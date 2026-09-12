using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Watch;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Models;
using InactivePDF.Domain.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using InactivePDF.Application.Models;
using InactivePDF.Application.Watermarks;
using InactivePDF.Application.Policies;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api.Workers;

public sealed class WatchFolderWorker(
    WatchFolderOptions options,
    IsolatedConversionWorker converter,
    IDiskSpaceGuard diskSpaceGuard,
    ResourcePolicy resourcePolicy,
    ILogger<WatchFolderWorker> logger,
    LiteDbJobStore jobs,
    WatermarkProfileStore watermarkProfiles) : BackgroundService
{
    private readonly StableFileDetector _stableFiles = new(options.EffectiveFileStabilityDelay);
    private readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inflightGate = new();
    private readonly HashSet<string> _reservedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _processingTasks = new();
    private readonly object _processingTasksGate = new();
    private readonly SemaphoreSlim _wakeScan = new(0, 1);
    private readonly SemaphoreSlim _markupConversionGate = new(Math.Max(1, options.MaximumMarkupConversions), Math.Max(1, options.MaximumMarkupConversions));
    private readonly ResourceAdmissionGate _resourceAdmissionGate = new(options.ResourceBudgetBytes, options.MaximumHeavyConversions);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WatchFolderLayout.Ensure(options);
        _ = PdfOutputProfileCatalog.Resolve(options.Profile);
        using var fileLogger = new WatchFolderLogger(options.LogPath);
        await fileLogger.WriteServiceAsync("service-started", $"root={options.RootPath}", CancellationToken.None).ConfigureAwait(false);
        await RecoverProcessingFilesAsync(fileLogger).ConfigureAwait(false);
        var monitorTask = options.ExecutionMode == ConversionExecutionMode.Development
            ? new ServiceResourceMonitor(fileLogger).RunAsync(stoppingToken)
            : Task.CompletedTask;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var path in ScheduleInputs())
                {
                    var fileName = Path.GetFileName(path);
                    if (IsInFlight(path) || !_stableFiles.IsStable(path)) continue;
                    var detectedAtUtc = _stableFiles.GetFirstSeenUtc(path);
                    if (!TryClaim(path, out var claimedPath, out var originalName)) continue;
                    StartProcessingTask(claimedPath, originalName, detectedAtUtc, DateTimeOffset.UtcNow, fileLogger, stoppingToken);
                }

                await _wakeScan.WaitAsync(options.EffectiveScanInterval, stoppingToken).ConfigureAwait(false);
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
        _markupConversionGate.Dispose();
        _resourceAdmissionGate.Dispose();
        _wakeScan.Dispose();
        base.Dispose();
    }

    private async Task ProcessAndForgetAsync(string path, string originalName, DateTimeOffset detectedAtUtc, DateTimeOffset claimedAtUtc, WatchFolderLogger fileLogger, CancellationToken cancellationToken)
    {
        try
        {
            await RecordJobAsync(path, originalName, detectedAtUtc, ConversionJobState.Accepted, 0).ConfigureAwait(false);
            await ProcessAsync(path, originalName, detectedAtUtc, claimedAtUtc, fileLogger, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryMove(path, options.InputPath, originalName);
            await RecordJobAsync(path, originalName, detectedAtUtc, ConversionJobState.Cancelled, 0, error: "service_stopped_input_returned").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryMove(path, options.ErrorsPath, originalName);
            await RecordJobAsync(path, originalName, detectedAtUtc, ConversionJobState.Failed, 0, error: exception.GetType().Name).ConfigureAwait(false);
            WatchLog.Failed(logger, ConversionFailureClassifier.Classify(exception).Code, originalName);
            await fileLogger.WriteAsync("error", originalName, false, 1, exception, detectedAtUtc: detectedAtUtc, claimedAtUtc: claimedAtUtc, completedAtUtc: DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        finally { lock (_inflightGate) _inflight.Remove(path); }
    }

    private void StartProcessingTask(string path, string originalName, DateTimeOffset detectedAtUtc, DateTimeOffset claimedAtUtc, WatchFolderLogger fileLogger, CancellationToken cancellationToken)
    {
        var task = ProcessAndForgetAsync(path, originalName, detectedAtUtc, claimedAtUtc, fileLogger, cancellationToken);
        lock (_processingTasksGate) _processingTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_processingTasksGate) _processingTasks.Remove(completed);
                _ = completed.Exception;
                try { _wakeScan.Release(); } catch (SemaphoreFullException) { }
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
            catch (Exception exception) { WatchLog.Failed(logger, ConversionFailureClassifier.Classify(exception).Code, "service-shutdown"); }
        }
    }

    private async Task ProcessAsync(string path, string originalName, DateTimeOffset detectedAtUtc, DateTimeOffset claimedAtUtc, WatchFolderLogger fileLogger, CancellationToken cancellationToken)
    {
        var fileName = originalName;
        var inputBytes = new FileInfo(path).Length;
        if (inputBytes > resourcePolicy.MaximumInputBytes)
            throw new IOException($"The input file exceeds the maximum allowed size of {resourcePolicy.MaximumInputBytes} bytes.");

        diskSpaceGuard.EnsureAvailable(options.RootPath, resourcePolicy.MinimumFreeDiskBytes);
        SemaphoreSlim? acquiredMarkupGate = null;
        ResourceAdmissionLease? acquiredResourceLease = null;
        var gateWaitMs = 0L;
        IsolatedWorkerMetrics? workerMetrics = null;
        var diagnosticsEnabled = options.ExecutionMode == ConversionExecutionMode.Development;
        var configuredWatermark = ResolveConfiguredWatermark();
        try
        {
            var reservationBytes = SelectResourceReservation(fileName, inputBytes);
            if (reservationBytes > 0)
            {
                var gateStarted = Stopwatch.GetTimestamp();
                acquiredResourceLease = await _resourceAdmissionGate.AcquireAsync(reservationBytes, cancellationToken).ConfigureAwait(false);
                gateWaitMs = (long)Stopwatch.GetElapsedTime(gateStarted).TotalMilliseconds;
            }

            if (IsMarkup(fileName))
            {
                var gateStarted = Stopwatch.GetTimestamp();
                await _markupConversionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateWaitMs += (long)Stopwatch.GetElapsedTime(gateStarted).TotalMilliseconds;
                acquiredMarkupGate = _markupConversionGate;
            }

            for (var attempt = 1; attempt <= options.MaximumRetries + 1; attempt++)
            {
                var started = Stopwatch.GetTimestamp();
                var conversionStartedAtUtc = DateTimeOffset.UtcNow;
                var cpuBeforeMs = diagnosticsEnabled ? (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds : 0;
                using var samplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var samplerTask = Task.CompletedTask;
                if (diagnosticsEnabled)
                {
                    var sampler = new ResourceSampler(fileLogger);
                    await fileLogger.WriteResourceAsync(fileName, "start", ResourceSnapshot.Capture(), cancellationToken).ConfigureAwait(false);
                    samplerTask = sampler.SampleAsync(fileName, samplerCancellation.Token);
                }
                string? output = null;
                try
                {
                    output = CreateOutputPath(fileName);
                    await RecordJobAsync(path, fileName, detectedAtUtc, ConversionJobState.Processing, attempt, inputBytes: inputBytes, processingMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ConfigureAwait(false);
                    workerMetrics = await converter.ConvertAsync(
                        new ConversionWorkerRequest(
                            ConversionOperation.ConvertFile,
                            output,
                            [new ConversionWorkerInput(path, fileName)],
                            Profile: options.Profile,
                            Watermark: configuredWatermark,
                            WatermarkProfile: options.WatermarkProfile, Source: "WatchFolder", JobId: WatchJobId(path).ToString(),
                            ExecutionMode: options.ExecutionMode),
                        cancellationToken).ConfigureAwait(false);
                    if (!TryMove(path, options.OriginalsPath, fileName))
                        throw new IOException($"Conversion completed but the source could not be moved to the originals folder: {options.OriginalsPath}");
                    var outputBytes = new FileInfo(output).Length;
                    await RecordJobAsync(path, fileName, detectedAtUtc, ConversionJobState.Succeeded, attempt, output, inputBytes: inputBytes, processingMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ConfigureAwait(false);
                    var resource = diagnosticsEnabled ? ResourceSnapshot.Capture().WithConversionWorker(workerMetrics) : null;
                    await fileLogger.WriteAsync("converted", fileName, true, attempt, durationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputBytes: inputBytes, outputBytes: outputBytes, workingSetBytes: resource?.WorkingSetBytes ?? 0, cpuMs: diagnosticsEnabled ? Math.Max(0, resource!.CpuMilliseconds - cpuBeforeMs) : 0, resource: resource, stages: diagnosticsEnabled ? workerMetrics.Stages : null, gateWaitMs: gateWaitMs, detectedAtUtc: detectedAtUtc, claimedAtUtc: claimedAtUtc, conversionStartedAtUtc: conversionStartedAtUtc, completedAtUtc: DateTimeOffset.UtcNow, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (attempt <= options.MaximumRetries && !cancellationToken.IsCancellationRequested && IsRetryable(exception))
                {
                    await RecordJobAsync(path, fileName, detectedAtUtc, ConversionJobState.Retrying, attempt, error: exception.GetType().Name, inputBytes: inputBytes, processingMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ConfigureAwait(false);
                    var resource = diagnosticsEnabled ? ResourceSnapshot.Capture() : null;
                    await fileLogger.WriteAsync("retry", fileName, false, attempt, exception, durationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputBytes: inputBytes, workingSetBytes: resource?.WorkingSetBytes ?? 0, cpuMs: diagnosticsEnabled ? Math.Max(0, resource!.CpuMilliseconds - cpuBeforeMs) : 0, resource: resource, gateWaitMs: gateWaitMs, detectedAtUtc: detectedAtUtc, claimedAtUtc: claimedAtUtc, conversionStartedAtUtc: conversionStartedAtUtc, completedAtUtc: DateTimeOffset.UtcNow, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << Math.Min(attempt - 1, 6))), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    TryMove(path, options.ErrorsPath, fileName);
                    await RecordJobAsync(path, fileName, detectedAtUtc, ConversionJobState.Failed, attempt, error: exception.GetType().Name, inputBytes: inputBytes, processingMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ConfigureAwait(false);
                    var resource = diagnosticsEnabled ? ResourceSnapshot.Capture() : null;
                    await fileLogger.WriteAsync("error", fileName, false, attempt, exception, durationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputBytes: inputBytes, workingSetBytes: resource?.WorkingSetBytes ?? 0, cpuMs: diagnosticsEnabled ? Math.Max(0, resource!.CpuMilliseconds - cpuBeforeMs) : 0, resource: resource, gateWaitMs: gateWaitMs, detectedAtUtc: detectedAtUtc, claimedAtUtc: claimedAtUtc, conversionStartedAtUtc: conversionStartedAtUtc, completedAtUtc: DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    WatchLog.Failed(logger, ConversionFailureClassifier.Classify(exception).Code, fileName);
                    return;
                }
                finally
                {
                    if (output is not null) lock (_inflightGate) _reservedOutputs.Remove(output);
                    samplerCancellation.Cancel();
                    try { await samplerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    if (diagnosticsEnabled)
                        await fileLogger.WriteResourceAsync(fileName, "end", ResourceSnapshot.Capture().WithConversionWorker(workerMetrics), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            acquiredMarkupGate?.Release();
            if (acquiredResourceLease is not null) await acquiredResourceLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool TryClaim(string inputPath, out string claimedPath, out string originalName)
    {
        originalName = Path.GetFileName(inputPath);
        claimedPath = Path.Combine(options.ProcessingPath, $"{Guid.NewGuid():N}-{originalName}");
        lock (_inflightGate) if (_inflight.Count >= options.MaximumConcurrentConversions) return false;
        try { File.Move(inputPath, claimedPath); _stableFiles.Forget(inputPath); lock (_inflightGate) _inflight.Add(claimedPath); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool IsInFlight(string path) { lock (_inflightGate) return _inflight.Contains(path); }

    private WatermarkOptions? ResolveConfiguredWatermark()
    {
        if (string.IsNullOrWhiteSpace(options.WatermarkProfile)) return null;
        if (!watermarkProfiles.TryGet(options.WatermarkProfile, out var profile))
            throw new ConversionFormatException("watermark_profile_not_found", $"The watch-folder watermark profile '{options.WatermarkProfile}' does not exist.");
        var errors = WatermarkProfileValidator.Validate(profile);
        if (errors.Count > 0)
            throw new ConversionFormatException("invalid_watermark_profile", string.Join(" ", errors));
        return profile;
    }

    private static bool IsHidden(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Length > 0 && fileName[0] == '.';
    }

    private static bool IsImage(string path) => SupportedFormatCatalog.IsImage(Path.GetExtension(path));

    private long SelectResourceReservation(string path, long inputBytes)
    {
        var extension = Path.GetExtension(path);
        if (IsMarkup(path)) return options.MarkupReservationBytes;
        if (IsImage(path)) return options.ImageReservationBytes;
        if (SupportedFormatCatalog.TryGet(extension, out var format) && format.Route == ConversionFormatRoute.LibreOffice)
            return options.OfficeReservationBytes;
        return inputBytes >= 1L * 1024 * 1024 ? options.LargeFileReservationBytes : 0;
    }

    private static bool IsMarkup(string path) => Path.GetExtension(path) is ".html" or ".htm" or ".rtf";

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

    private IEnumerable<string> ScheduleInputs()
    {
        var lanes = Directory.EnumerateFiles(options.InputPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !IsHidden(path) && IsSafeWatchFile(path)).GroupBy(GetPriority).OrderBy(group => group.Key)
            .Select(group => new Queue<string>(group.OrderBy(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))).ToArray();
        while (lanes.Any(lane => lane.Count > 0))
            foreach (var lane in lanes) if (lane.TryDequeue(out var path)) yield return path;
    }

    private async Task RecoverProcessingFilesAsync(WatchFolderLogger fileLogger)
    {
        foreach (var path in Directory.EnumerateFiles(options.ProcessingPath, "*", SearchOption.TopDirectoryOnly).Where(IsSafeWatchFile))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Length == 0 || fileName[0] == '.') continue;
            var originalName = GetOriginalName(fileName);
            var inputBytes = new FileInfo(path).Length;
            if (TryMove(path, options.InputPath, originalName))
            {
                if (fileName.Length > 33 && Guid.TryParseExact(fileName[..32], "N", out _))
                    await RecordJobAsync(path, originalName, DateTimeOffset.UtcNow, ConversionJobState.Cancelled, 0, error: "recovered_after_restart_requeued").ConfigureAwait(false);
                await fileLogger.WriteAsync("recovered", originalName, true, 0, inputBytes: inputBytes, resource: ResourceSnapshot.Capture(), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private string CreateOutputPath(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        lock (_inflightGate)
        {
            var path = Path.Combine(options.OutputPath, baseName + ".pdf");
            if (File.Exists(path) || !_reservedOutputs.Add(path))
            {
                path = Path.Combine(options.OutputPath, $"{baseName}-{Guid.NewGuid():N}.pdf");
                _reservedOutputs.Add(path);
            }
            return path;
        }
    }

    private static Guid WatchJobId(string path) => Guid.ParseExact(Path.GetFileName(path)[..32], "N");

    private Task RecordJobAsync(string path, string name, DateTimeOffset accepted, ConversionJobState state, int attempt, string? output = null, string? error = null, long inputBytes = 0, long processingMilliseconds = 0) {
        var outputBytes = output is not null && File.Exists(output) ? new FileInfo(output).Length : 0;
        var compression = inputBytes == 0 || outputBytes == 0 ? (double?)null : (inputBytes - outputBytes) * 100d / inputBytes;
        var format = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return jobs.UpsertAsync(new JobStatus(WatchJobId(path), "watch:" + name, ConversionOperation.ConvertFile, state, accepted, DateTimeOffset.UtcNow, attempt, output, error,
            Source: "WatchFolder", Format: string.IsNullOrWhiteSpace(format) ? "unknown" : format, ProgressPercent: state == ConversionJobState.Succeeded ? 100 : 0,
            QueueWaitMilliseconds: Math.Max(0, (long)(DateTimeOffset.UtcNow - accepted).TotalMilliseconds - processingMilliseconds), ProcessingMilliseconds: processingMilliseconds,
            InputBytes: inputBytes, OutputBytes: outputBytes, CompressionPercent: compression, RetryCount: Math.Max(0, attempt - 1),
            Profile: options.Profile, WatermarkProfile: options.WatermarkProfile));
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

    private bool IsSafeWatchFile(string path)
    {
        try
        {
            WorkspacePathSecurity.EnsureSafeChain(path, options.RootPath);
            return !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal static partial class WatchLog
{
    [LoggerMessage(LogLevel.Error, "Watch-folder conversion failed for {FileName}; code={ErrorCode}")]
    public static partial void Failed(ILogger logger, string errorCode, string fileName);
}
