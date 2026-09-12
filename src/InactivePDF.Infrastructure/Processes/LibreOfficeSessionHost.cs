using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InactivePDF.Domain.Models;
using Microsoft.Extensions.Logging;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Owns Windows-only persistent LibreOffice sessions. Each session has its own profile,
/// process boundary and job object. Non-Windows callers receive no lease and retain the
/// existing short-lived conversion path.
/// </summary>
public sealed partial class LibreOfficeSessionHost(ILogger<LibreOfficeSessionHost> logger) : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _availableSlots = new(Math.Max(1, ResolvePoolSize()), Math.Max(1, ResolvePoolSize()));
    private readonly List<SessionSlot> _slots = [];
    private int _activeLeases;
    private long _generation;
    private int _stopped;
    private int _disposed;

    public async ValueTask<LibreOfficeSessionLease?> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !IsEnabled() || Volatile.Read(ref _stopped) != 0 || Volatile.Read(ref _disposed) != 0) return null;

        await _availableSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var slot = _slots.FirstOrDefault(candidate => !candidate.IsBusy);
                if (slot is null)
                {
                    slot = new SessionSlot(_slots.Count);
                    _slots.Add(slot);
                }

                if (!await EnsureStartedAsync(slot, cancellationToken).ConfigureAwait(false))
                {
                    _availableSlots.Release();
                    return null;
                }

                slot.IsBusy = true;
                _activeLeases++;
                _generation++;
                return new LibreOfficeSessionLease(slot.ProfilePath!, slot.Process!.Id, () => ReleaseAsync(slot));
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch
        {
            _availableSlots.Release();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var slot in _slots) StopSession(slot);
            _activeLeases = 0;
        }
        finally { _lifecycle.Release(); }
    }

    public object Snapshot()
    {
        _lifecycle.Wait();
        try
        {
            var configured = Math.Max(1, ResolvePoolSize());
            var slots = _slots.Select(slot => new
            {
                slot = slot.Index,
                ready = slot.Process is { HasExited: false },
                busy = slot.IsBusy,
                processId = slot.Process is { HasExited: false } process ? process.Id : (int?)null
            }).ToArray();
            return new
            {
                enabled = OperatingSystem.IsWindows() && IsEnabled(),
                configured,
                ready = slots.Count(slot => slot.ready),
                busy = slots.Count(slot => slot.busy),
                available = Math.Max(0, configured - slots.Count(slot => slot.busy)),
                slots
            };
        }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifecycle.Wait();
        try
        {
            foreach (var slot in _slots) StopSession(slot);
            _activeLeases = 0;
        }
        finally
        {
            _lifecycle.Release();
            _availableSlots.Dispose();
            _lifecycle.Dispose();
        }
    }

    private async Task<bool> EnsureStartedAsync(SessionSlot slot, CancellationToken cancellationToken)
    {
        if (slot.Process is { HasExited: false } && slot.ProfilePath is not null) return true;
        StopSession(slot);

        var options = LibreOfficeOptions.Default with { SharedProfilePath = null };
        if (!File.Exists(options.ExecutablePath) && !CanResolveExecutable(options.ExecutablePath)) return false;

        slot.RootPath = Path.Combine(Path.GetTempPath(), $"inactivepdf-office-{Guid.NewGuid():N}");
        slot.ProfilePath = Path.Combine(slot.RootPath, "profile");
        Directory.CreateDirectory(slot.ProfilePath);
        LibreOfficeProfileProvisioner.Create(slot.ProfilePath);
        slot.Port = FindFreePort();

        var startInfo = new ProcessStartInfo(ResolveSessionExecutable(options.ExecutablePath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = slot.RootPath
        };
        foreach (var argument in new[]
        {
            "--headless", "--invisible", "--nodefault", "--nologo", "--nolockcheck", "--norestore",
            "--nofirststartwizard", $"-env:UserInstallation={ToFileUri(slot.ProfilePath)}",
            $"--accept=socket,host=127.0.0.1,port={slot.Port};urp;StarOffice.ComponentContext"
        })
            startInfo.ArgumentList.Add(argument);

        try
        {
            slot.Process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!slot.Process.Start())
            {
                StopSession(slot);
                return false;
            }

            _ = slot.Process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = slot.Process.StandardError.ReadToEndAsync(CancellationToken.None);
            slot.Job = WindowsJobObject.Attach(slot.Process, ResolveSessionMemoryLimit());
            if (slot.Job is null)
            {
                SessionLog.JobAttachmentFailed(logger, slot.Process.Id);
                StopSession(slot);
                return false;
            }

            if (!await WaitForListenerAsync(slot.Process, slot.Port, cancellationToken).ConfigureAwait(false))
            {
                SessionLog.ReadinessFailed(logger, slot.Process.Id);
                StopSession(slot);
                return false;
            }

            SessionLog.Started(logger, slot.Index, slot.Process.Id);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or SocketException or System.ComponentModel.Win32Exception)
        {
            SessionLog.StartFailed(logger, exception.GetType().Name);
            StopSession(slot);
            return false;
        }
        catch (OperationCanceledException)
        {
            StopSession(slot);
            throw;
        }
    }

    private async ValueTask ReleaseAsync(SessionSlot slot)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        long generation;
        var shouldScheduleStop = false;
        try
        {
            if (slot.IsBusy)
            {
                slot.IsBusy = false;
                if (_activeLeases > 0) _activeLeases--;
            }
            generation = ++_generation;
            shouldScheduleStop = _activeLeases == 0;
        }
        finally { _lifecycle.Release(); }

        if (Volatile.Read(ref _disposed) == 0)
            _availableSlots.Release();
        if (shouldScheduleStop) _ = StopWhenIdleAsync(generation);
    }

    private async Task StopWhenIdleAsync(long generation)
    {
        var seconds = ParsePositive(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_IDLE_TIMEOUT_SECONDS"), 60);
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        if (Volatile.Read(ref _stopped) != 0 || Volatile.Read(ref _disposed) != 0) return;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeLeases == 0 && generation == _generation)
                foreach (var slot in _slots) StopSession(slot);
        }
        finally { _lifecycle.Release(); }
    }

    private static void StopSession(SessionSlot slot)
    {
        if (slot.Process is not null)
        {
            try
            {
                if (!slot.Process.HasExited) slot.Process.Kill(entireProcessTree: true);
                slot.Process.WaitForExit(5_000);
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                slot.Process.Dispose();
                slot.Process = null;
            }
        }

        slot.Job?.Dispose();
        slot.Job = null;
        if (slot.RootPath is not null)
        {
            try { if (Directory.Exists(slot.RootPath)) Directory.Delete(slot.RootPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        slot.RootPath = null;
        slot.ProfilePath = null;
        slot.Port = 0;
        slot.IsBusy = false;
    }

    private static async Task<bool> WaitForListenerAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 15);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SocketException) { }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ToFileUri(string path) => new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
    private static string ResolveSessionExecutable(string configuredPath)
    {
        if (!OperatingSystem.IsWindows() || !configuredPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return configuredPath;
        var consoleLauncher = Path.ChangeExtension(configuredPath, ".com");
        return File.Exists(consoleLauncher) ? consoleLauncher : configuredPath;
    }
    private static bool CanResolveExecutable(string path) => !Path.IsPathRooted(path) && Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Any(directory => File.Exists(Path.Combine(directory, path))) is true;
    private static bool IsEnabled() => !bool.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_WINDOWS_PERSISTENT_OFFICE"), out var enabled) || enabled;
    private static int ResolvePoolSize() => ParsePositive(Environment.GetEnvironmentVariable("INACTIVEPDF_OFFICE_CONCURRENCY"), 1);
    private static long ResolveSessionMemoryLimit() => long.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_WORKER_MAX_MEMORY_BYTES"), out var bytes) && bytes > 0 ? bytes : 0;
    private static int ParsePositive(string? value, int fallback) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private sealed class SessionSlot(int index)
    {
        public int Index { get; } = index;
        public Process? Process { get; set; }
        public WindowsJobObject? Job { get; set; }
        public string? RootPath { get; set; }
        public string? ProfilePath { get; set; }
        public int Port { get; set; }
        public bool IsBusy { get; set; }
    }

    private static partial class SessionLog
    {
        [LoggerMessage(LogLevel.Information, "LibreOffice Windows session slot {Slot} started with process {ProcessId}")]
        public static partial void Started(ILogger logger, int slot, int processId);

        [LoggerMessage(LogLevel.Warning, "LibreOffice Windows session process {ProcessId} could not be attached to a Windows Job Object")]
        public static partial void JobAttachmentFailed(ILogger logger, int processId);

        [LoggerMessage(LogLevel.Warning, "LibreOffice Windows session process {ProcessId} did not become ready")]
        public static partial void ReadinessFailed(ILogger logger, int processId);

        [LoggerMessage(LogLevel.Warning, "LibreOffice Windows session could not be started; using isolated cold starts; exceptionType={ExceptionType}")]
        public static partial void StartFailed(ILogger logger, string exceptionType);
    }
}
