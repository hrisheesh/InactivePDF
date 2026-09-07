using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Watch;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Owns the process boundary for every document conversion. The parent process only
/// writes a small manifest and exchanges file paths with the child process.
/// </summary>
public sealed class IsolatedConversionWorker(ConversionWorkerOptions options)
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
        ValidateRequest(request);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var requestPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".inactivepdf-worker-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        using var process = new Process
        {
            StartInfo = BuildStartInfo(requestPath),
            EnableRaisingEvents = true
        };

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
                var output = await standardOutput.ConfigureAwait(false);
                var error = await standardError.ConfigureAwait(false);
                throw new ConversionWorkerTimeoutException($"The isolated conversion worker exceeded {options.EffectiveWorkerTimeout}. ProcessId={process.Id}. StandardOutput={output.Trim()} StandardError={error.Trim()}");
            }

            var standardOutputText = await standardOutput.ConfigureAwait(false);
            var standardErrorText = await standardError.ConfigureAwait(false);
            if (tracker.ResourceLimitExceeded)
            {
                throw new ConversionResourceLimitException($"The isolated conversion worker exceeded the working-set limit of {options.MaximumWorkerWorkingSetBytes} bytes. ProcessId={process.Id}. StandardOutput={standardOutputText.Trim()} StandardError={standardErrorText.Trim()}");
            }

            if (process.ExitCode != 0)
            {
                var diagnostics = $"StandardOutput={standardOutputText.Trim()} StandardError={standardErrorText.Trim()}";
                if (TryReadFormatErrorCode(standardErrorText, out var code))
                {
                    throw new ConversionFormatException(
                        code,
                        $"The conversion worker rejected the input. {diagnostics}");
                }

                var retryable = !ContainsPermanentInputFailure(diagnostics);
                throw new ConversionWorkerExecutionException($"The isolated conversion worker exited with code {process.ExitCode}. ProcessId={process.Id}. {diagnostics}", retryable);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new ConversionWorkerExecutionException($"The isolated conversion worker exited successfully but did not produce a non-empty PDF at {outputPath}.");
            }

            return tracker!.Snapshot;
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
        }
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
        foreach (var input in request.Inputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.FileName);
            var fullInput = Path.GetFullPath(input.Path);
            if (!File.Exists(fullInput)) throw new FileNotFoundException("The worker input does not exist.", fullInput);
            if (string.Equals(fullInput, output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The worker output must be different from every input.", nameof(request));
        }
    }

    private static string ResolveWorkerPath() =>
        TryResolveWorkerPath()
        ?? throw new FileNotFoundException("The named conversion worker was not found. Set INACTIVEPDF_CONVERSION_WORKER_PATH or build InactivePDF.ConversionWorker.");

    private static string? TryResolveWorkerPath()
    {
        var configured = Environment.GetEnvironmentVariable("INACTIVEPDF_CONVERSION_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var fileName = OperatingSystem.IsWindows() ? "InactivePDF.ConversionWorker.exe" : "InactivePDF.ConversionWorker";
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InactivePDF.ConversionWorker/bin/Debug/net10.0", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InactivePDF.ConversionWorker/bin/Release/net10.0", fileName)),
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
        private int _resourceLimitExceeded;

        public bool ResourceLimitExceeded => Volatile.Read(ref _resourceLimitExceeded) == 1;

        public IsolatedWorkerMetrics Snapshot => new(
            process.Id,
            Interlocked.Read(ref _peakWorkingSetBytes),
            Interlocked.Read(ref _peakPhysicalFootprintBytes),
            Volatile.Read(ref _peakThreadCount),
            Interlocked.Read(ref _cpuMilliseconds),
            Interlocked.Read(ref _peakProcessTreeMemoryBytes));

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

        private void Observe()
        {
            try
            {
                var workingSet = process.WorkingSet64;
                InterlockedMax(ref _peakWorkingSetBytes, workingSet);
                InterlockedMax(ref _peakPhysicalFootprintBytes, ProcessMemoryMetrics.Capture(process.Id).CurrentBytes);
                var processTreeMemory = job?.PeakJobMemoryBytes ?? CaptureKnownProcessTreeMemory(process, _workerStartTime);
                InterlockedMax(ref _peakProcessTreeMemoryBytes, processTreeMemory);
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
                    if (candidate.Id == worker.Id || (candidate.Id != sessionPid && candidate.StartTime < workerStartTime)) continue;
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
    long PeakProcessTreeMemoryBytes);
