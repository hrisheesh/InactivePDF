using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Resources;
using InactivePDF.Infrastructure.Watch;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Owns the process boundary for every document conversion. The parent process only
/// writes a small manifest and exchanges file paths with the child process.
/// </summary>
public sealed class IsolatedConversionWorker(
    ConversionWorkerOptions options,
    ConversionTelemetryStore? telemetry = null,
    SwarmScheduler? scheduler = null,
    LibreOfficeSessionHost? officeSession = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool IsWorkerAvailable => TryResolveWorkerPath() is not null;

    public async Task<IsolatedWorkerMetrics> ConvertAsync(
        ConversionWorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var admission = scheduler is null ? null : await scheduler.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        LibreOfficeSessionLease? officeLease = null;
        try
        {
            if (officeSession is not null && IsWindowsOfficeRequest(request) && string.IsNullOrWhiteSpace(request.LibreOfficeSessionProfilePath))
                officeLease = await officeSession.AcquireAsync(cancellationToken).ConfigureAwait(false);
            request = request with
            {
                LibreOfficeSessionProfilePath = officeLease?.ProfilePath ?? request.LibreOfficeSessionProfilePath,
                LibreOfficeSessionProcessId = officeLease?.ProcessId ?? request.LibreOfficeSessionProcessId
            };

            var observation = telemetry?.Begin(request);
            if (observation is not null) { observation.QueueWaitMs = admission?.WaitMs ?? 0; observation.WorkerId = admission?.WorkerId; }
            var started = Stopwatch.GetTimestamp();
            Exception? failure = null;
            IsolatedWorkerMetrics? metrics = null;
            try { metrics = await ConvertCoreAsync(request, cancellationToken).ConfigureAwait(false); return metrics; }
            catch (Exception error) { failure = error; if (admission is not null) admission.Error = error; throw; }
            finally { telemetry?.Complete(observation, Stopwatch.GetElapsedTime(started).TotalMilliseconds, request.OutputPath, failure, metrics); }
        }
        finally
        {
            if (officeLease is not null) await officeLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IsolatedWorkerMetrics> ConvertCoreAsync(
        ConversionWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var requestPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".inactivepdf-worker-{Guid.NewGuid():N}.json");
        var telemetryPath = request.ExecutionMode == ConversionExecutionMode.Development ? requestPath + ".metrics" : null;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request with { TelemetryPath = telemetryPath }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        using var process = new Process
        {
            StartInfo = BuildStartInfo(requestPath),
            EnableRaisingEvents = true
        };

        ConversionWorkerEnvironment.Apply(process.StartInfo.Environment, request);

        IsolatedWorkerTracker? tracker = null;
        CancellationTokenSource? trackerCancellation = null;
        Task? trackerTask = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The isolated conversion worker failed to start.");

            var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var job = WindowsJobObject.Attach(process, options.MaximumWorkerWorkingSetBytes);
            tracker = new IsolatedWorkerTracker(process, options.MaximumWorkerWorkingSetBytes, job);
            trackerCancellation = new CancellationTokenSource();
            trackerTask = tracker.RunAsync(trackerCancellation.Token);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.EffectiveWorkerTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                _ = await standardOutput.ConfigureAwait(false);
                _ = await standardError.ConfigureAwait(false);
                throw new ConversionWorkerTimeoutException("The isolated conversion worker exceeded its configured time limit.");
            }

            _ = await standardOutput.ConfigureAwait(false);
            var standardErrorText = await standardError.ConfigureAwait(false);
            tracker.CaptureFinal();
            if (tracker.ResourceLimitExceeded)
            {
                throw new ConversionResourceLimitException("The isolated conversion worker exceeded its configured resource limit.");
            }

            if (process.ExitCode != 0)
            {
                if (TryReadFormatErrorCode(standardErrorText, out var code))
                {
                    throw new ConversionFormatException(
                        code,
                        "The conversion worker rejected the input.");
                }

                var retryable = !ContainsPermanentInputFailure(standardErrorText);
                throw new ConversionWorkerExecutionException("The isolated conversion worker failed.", retryable);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new ConversionWorkerExecutionException("The isolated conversion worker did not produce a non-empty PDF.", isRetryable: false);
            }

            return tracker!.Snapshot with { Stages = telemetryPath is null ? null : await ReadStageReportAsync(telemetryPath).ConfigureAwait(false) };
        }
        finally
        {
            if (trackerCancellation is not null)
            {
                trackerCancellation.Cancel();
                if (trackerTask is not null)
                {
                    try { await trackerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                trackerCancellation.Dispose();
            }
            TryDelete(requestPath);
            if (telemetryPath is not null) TryDelete(telemetryPath);
        }
    }

    private static async Task<ConversionStageReport?> ReadStageReportAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stages = await JsonSerializer.DeserializeAsync<Dictionary<string, ConversionStageTiming>>(stream, JsonOptions).ConfigureAwait(false);
            return stages is null ? null : new ConversionStageReport(stages);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static ProcessStartInfo BuildStartInfo(string requestPath)
    {
        var startInfo = new ProcessStartInfo(ResolveWorkerPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add(ConversionWorkerRunner.Switch);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(Path.GetFullPath(requestPath));
        startInfo.Environment[ConversionWorkerRunner.EnvironmentVariable] = "1";
        return startInfo;
    }

    private static void ValidateRequest(ConversionWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        if (request.Inputs is null || request.Inputs.Count == 0)
            throw new ArgumentException("At least one worker input is required.", nameof(request));

        var output = Path.GetFullPath(request.OutputPath);
        WorkspacePathSecurity.EnsureSafeChain(output, Path.GetDirectoryName(output)!);
        foreach (var input in request.Inputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.FileName);
            var fullInput = Path.GetFullPath(input.Path);
            WorkspacePathSecurity.EnsureSafeChain(fullInput, Path.GetDirectoryName(fullInput)!);
            if (!File.Exists(fullInput)) throw new FileNotFoundException("The worker input does not exist.", fullInput);
            if (string.Equals(fullInput, output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The worker output must be different from every input.", nameof(request));
        }
    }

    private static bool IsWindowsOfficeRequest(ConversionWorkerRequest request) =>
        OperatingSystem.IsWindows() && request.Inputs.Any(input =>
            SupportedFormatCatalog.TryGet(Path.GetExtension(input.FileName), out var format) && format.Route == ConversionFormatRoute.LibreOffice);

    private static string ResolveWorkerPath() =>
        TryResolveWorkerPath()
        ?? throw new FileNotFoundException("The named conversion worker was not found. Set INACTIVEPDF_CONVERSION_WORKER_PATH or build InactivePDF.ConversionWorker.");

    private static string? TryResolveWorkerPath()
    {
        var configured = Environment.GetEnvironmentVariable("INACTIVEPDF_CONVERSION_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var fileName = OperatingSystem.IsWindows() ? "InactivePDF.ConversionWorker.exe" : "InactivePDF.ConversionWorker";
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InactivePDF.ConversionWorker/bin/Release/net10.0", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/InactivePDF.ConversionWorker/bin/Release/net10.0", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InactivePDF.ConversionWorker/bin/Debug/net10.0", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/InactivePDF.ConversionWorker/bin/Debug/net10.0", fileName)),
                Path.Combine(AppContext.BaseDirectory, fileName)
            }
            : new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InactivePDF.ConversionWorker/bin/Debug/net10.0", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InactivePDF.ConversionWorker/bin/Release/net10.0", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/InactivePDF.ConversionWorker/bin/Debug/net10.0", fileName)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/InactivePDF.ConversionWorker/bin/Release/net10.0", fileName)),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        catch (OperationCanceledException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool ContainsPermanentInputFailure(string diagnostics) =>
        diagnostics.Contains(nameof(ConversionFormatException), StringComparison.Ordinal) ||
        diagnostics.Contains(nameof(NotSupportedException), StringComparison.Ordinal) ||
        diagnostics.Contains(nameof(InvalidDataException), StringComparison.Ordinal) ||
        diagnostics.Contains("PdfReaderException", StringComparison.Ordinal) ||
        diagnostics.Contains("PdfException", StringComparison.Ordinal) ||
        diagnostics.Contains(nameof(ArgumentException), StringComparison.Ordinal) ||
        diagnostics.Contains(nameof(FileNotFoundException), StringComparison.Ordinal) ||
        diagnostics.Contains(nameof(DirectoryNotFoundException), StringComparison.Ordinal) ||
        diagnostics.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal);

    private static bool TryReadFormatErrorCode(string diagnostics, out string code)
    {
        const string marker = "INACTIVEPDF_ERROR_CODE=";
        var start = diagnostics.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            code = string.Empty;
            return false;
        }

        start += marker.Length;
        var end = diagnostics.IndexOfAny(['\r', '\n'], start);
        code = (end < 0 ? diagnostics[start..] : diagnostics[start..end]).Trim();
        return code.Length > 0 && code.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
    }

    private sealed class IsolatedWorkerTracker(Process process, long maximumWorkingSetBytes, WindowsJobObject? job)
    {
        private readonly DateTime _workerStartTime = TryGetStartTime(process);
        private long _peakWorkingSetBytes;
        private long _peakPhysicalFootprintBytes;
        private int _peakThreadCount;
        private long _cpuMilliseconds;
        private long _peakProcessTreeMemoryBytes;
        private long _processTreeCpuMilliseconds;
        private int _peakProcessCount;
        private int _resourceLimitExceeded;

        public bool ResourceLimitExceeded => Volatile.Read(ref _resourceLimitExceeded) == 1;

        public IsolatedWorkerMetrics Snapshot => new(
            process.Id,
            Interlocked.Read(ref _peakWorkingSetBytes),
            Interlocked.Read(ref _peakPhysicalFootprintBytes),
            Volatile.Read(ref _peakThreadCount),
            Interlocked.Read(ref _cpuMilliseconds),
            Interlocked.Read(ref _peakProcessTreeMemoryBytes),
            Interlocked.Read(ref _processTreeCpuMilliseconds),
            Volatile.Read(ref _peakProcessCount));

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Observe();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (process.HasExited) break;
                    Observe();
                }
                catch (InvalidOperationException) { break; }
                catch (ArgumentException) { break; }
                catch (System.ComponentModel.Win32Exception) { break; }
            }
        }

        public void CaptureFinal()
        {
            try
            {
                process.Refresh();
                InterlockedMax(ref _cpuMilliseconds, (long)process.TotalProcessorTime.TotalMilliseconds);
                var processTree = CaptureKnownProcessTree(process, _workerStartTime);
                InterlockedMax(ref _processTreeCpuMilliseconds, Math.Max(job?.TotalCpuMilliseconds ?? 0, processTree.CpuMilliseconds));
                InterlockedMax(ref _peakProcessCount, processTree.ProcessCount);
                InterlockedMax(ref _peakProcessTreeMemoryBytes, job?.PeakJobMemoryBytes ?? CaptureKnownProcessTreeMemory(process, _workerStartTime));
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void Observe()
        {
            try
            {
                var workingSet = process.WorkingSet64;
                InterlockedMax(ref _peakWorkingSetBytes, workingSet);
                InterlockedMax(ref _peakPhysicalFootprintBytes, ProcessMemoryMetrics.Capture(process.Id).CurrentBytes);
                var processTreeMemory = job?.PeakJobMemoryBytes ?? CaptureKnownProcessTreeMemory(process, _workerStartTime);
                InterlockedMax(ref _peakProcessTreeMemoryBytes, processTreeMemory);
                var processTree = CaptureKnownProcessTree(process, _workerStartTime);
                InterlockedMax(ref _processTreeCpuMilliseconds, Math.Max(job?.TotalCpuMilliseconds ?? 0, processTree.CpuMilliseconds));
                InterlockedMax(ref _peakProcessCount, processTree.ProcessCount);
                InterlockedMax(ref _peakThreadCount, process.Threads.Count);
                InterlockedMax(ref _cpuMilliseconds, (long)process.TotalProcessorTime.TotalMilliseconds);
                if (maximumWorkingSetBytes > 0 && (workingSet > maximumWorkingSetBytes || processTreeMemory > maximumWorkingSetBytes) && Interlocked.Exchange(ref _resourceLimitExceeded, 1) == 0)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (ArgumentException) { }
                }
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void InterlockedMax(ref long target, long value)
        {
            while (true)
            {
                var current = Interlocked.Read(ref target);
                if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }

        private static long CaptureKnownProcessTreeMemory(Process worker, DateTime workerStartTime)
        {
            var total = ProcessMemoryMetrics.Capture(worker.Id).CurrentBytes;
            var observed = new HashSet<int> { worker.Id };
            var sessionPid = int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PID"), out var configuredPid)
                ? configuredPid
                : 0;
            foreach (var candidate in Process.GetProcessesByName("soffice").Concat(Process.GetProcessesByName("soffice.bin")))
            {
                try
                {
                    if (candidate.Id == worker.Id || (sessionPid > 0 ? candidate.Id != sessionPid : candidate.StartTime < workerStartTime)) continue;
                    if (observed.Add(candidate.Id))
                        total = checked(total + ProcessMemoryMetrics.Capture(candidate.Id).CurrentBytes);
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                finally { candidate.Dispose(); }
            }

            if (sessionPid > 0 && observed.Add(sessionPid))
            {
                try { total = checked(total + ProcessMemoryMetrics.Capture(sessionPid).CurrentBytes); }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
            return total;
        }

        private static (long CpuMilliseconds, int ProcessCount) CaptureKnownProcessTree(Process worker, DateTime workerStartTime)
        {
            var cpu = (long)worker.TotalProcessorTime.TotalMilliseconds;
            var count = 1;
            var sessionPid = int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PID"), out var configuredPid) ? configuredPid : 0;
            foreach (var candidate in Process.GetProcessesByName("soffice").Concat(Process.GetProcessesByName("soffice.bin")))
            {
                try
                {
                    if (candidate.Id == worker.Id || (sessionPid > 0 ? candidate.Id != sessionPid : candidate.StartTime < workerStartTime)) continue;
                    cpu = checked(cpu + (long)candidate.TotalProcessorTime.TotalMilliseconds);
                    count++;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                finally { candidate.Dispose(); }
            }
            return (cpu, count);
        }

        private static DateTime TryGetStartTime(Process process)
        {
            try { return process.StartTime; }
            catch (InvalidOperationException) { return DateTime.UtcNow; }
            catch (ArgumentException) { return DateTime.UtcNow; }
            catch (System.ComponentModel.Win32Exception) { return DateTime.UtcNow; }
        }
    }
}

public sealed record IsolatedWorkerMetrics(
    int ProcessId,
    long PeakWorkingSetBytes,
    long PeakPhysicalFootprintBytes,
    int PeakThreadCount,
    long CpuMilliseconds,
    long PeakProcessTreeMemoryBytes,
    long ProcessTreeCpuMilliseconds = 0,
    int PeakProcessCount = 0,
    ConversionStageReport? Stages = null);
