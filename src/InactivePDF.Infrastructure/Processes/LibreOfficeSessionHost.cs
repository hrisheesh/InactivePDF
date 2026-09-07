using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InactivePDF.Domain.Models;
using Microsoft.Extensions.Logging;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Owns an optional warm LibreOffice listener. The listener is lazy, isolated by a private
/// profile, and stopped after an idle period so an idle service does not retain office memory.
/// Individual conversions still run through the isolated worker boundary.
/// </summary>
public sealed partial class LibreOfficeSessionHost(ILogger<LibreOfficeSessionHost> logger) : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private string? _profilePath;
    private string? _sessionRoot;
    private int _activeLeases;
    private long _generation;
    private int _disposed;

    public async ValueTask<IAsyncDisposable?> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryStartSession()) return null;
            _activeLeases++;
            _generation++;
            return new Lease(this);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { StopSession(); }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifecycle.Wait();
        try { StopSession(); }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private bool TryStartSession()
    {
        if (_process is { HasExited: false } && _profilePath is not null) return true;
        StopSession();

        var options = LibreOfficeOptions.Default;
        if (!File.Exists(options.ExecutablePath) && !CanResolveExecutable(options.ExecutablePath))
            return false;

        _sessionRoot = Path.Combine(Path.GetTempPath(), $"inactivepdf-office-{Guid.NewGuid():N}");
        _profilePath = Path.Combine(_sessionRoot, "profile");
        Directory.CreateDirectory(_profilePath);
        LibreOfficeProfileProvisioner.Create(_profilePath);

        var port = FindFreePort();
        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _sessionRoot
        };
        foreach (var argument in new[]
        {
            "--headless", "--invisible", "--nodefault", "--nologo", "--nolockcheck", "--norestore",
            "--nofirststartwizard", $"-env:UserInstallation={ToFileUri(_profilePath)}",
            $"--accept=socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext"
        })
            startInfo.ArgumentList.Add(argument);

        try
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                StopSession();
                return false;
            }

            _ = _process.StandardOutput.ReadToEndAsync();
            _ = _process.StandardError.ReadToEndAsync();
            Thread.Sleep(500);
            if (_process.HasExited)
            {
                SessionLog.StartupExited(logger, _process.ExitCode);
                StopSession();
                return false;
            }

            Environment.SetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE", _profilePath);
            Environment.SetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PID", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or SocketException)
        {
            SessionLog.StartFailed(logger, exception);
            StopSession();
            return false;
        }
    }

    private async ValueTask ReleaseAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        long generation;
        var shouldScheduleStop = false;
        try
        {
            if (_activeLeases > 0) _activeLeases--;
            generation = ++_generation;
            shouldScheduleStop = _activeLeases == 0;
        }
        finally { _lifecycle.Release(); }

        if (shouldScheduleStop)
            _ = StopWhenIdleAsync(generation);
    }

    private async Task StopWhenIdleAsync(long generation)
    {
        var seconds = ParsePositive(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_IDLE_TIMEOUT_SECONDS"), 60);
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeLeases == 0 && generation == _generation) StopSession();
        }
        finally { _lifecycle.Release(); }
    }

    private void StopSession()
    {
        var processId = _process?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        if (_profilePath is not null && string.Equals(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE"), _profilePath, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE", null);
        if (string.Equals(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PID"), processId, StringComparison.Ordinal))
            Environment.SetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PID", null);

        if (_sessionRoot is not null)
        {
            try { if (Directory.Exists(_sessionRoot)) Directory.Delete(_sessionRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _sessionRoot = null;
            _profilePath = null;
        }
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ToFileUri(string path) => new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

    private static bool CanResolveExecutable(string path) =>
        !Path.IsPathRooted(path) && Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Any(directory => File.Exists(Path.Combine(directory, path))) is true;

    private static int ParsePositive(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private sealed class Lease(LibreOfficeSessionHost owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseAsync() : ValueTask.CompletedTask;
    }

    private static partial class SessionLog
    {
        [LoggerMessage(LogLevel.Warning, "LibreOffice warm session exited during startup with code {ExitCode}")]
        public static partial void StartupExited(ILogger logger, int exitCode);

        [LoggerMessage(LogLevel.Warning, "LibreOffice warm session could not be started; using isolated cold starts")]
        public static partial void StartFailed(ILogger logger, Exception exception);
    }
}
