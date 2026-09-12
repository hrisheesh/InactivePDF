using System.Globalization;
using System.Text.Json;
using InactivePDF.Application.Policies;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Infrastructure.Watch;

public sealed class WatchFolderLogger : IDisposable
{
    private const int SchemaVersion = 1;
    private const int FlushBatchSize = 32;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly StreamWriter _detailedWriter;
    private readonly StreamWriter _userWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pendingLines;
    private DateTimeOffset _lastFlushUtc = DateTimeOffset.UtcNow;

    public WatchFolderLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var date = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        _detailedWriter = CreateWriter(Path.Combine(logDirectory, $"InactivePDF-{date}.jsonl"));
        _userWriter = CreateWriter(Path.Combine(logDirectory, $"InactivePDF-{date}.log"));
    }

    public async Task WriteAsync(string eventName, string path, bool success, int attempt, Exception? exception = null, long durationMs = 0, long inputBytes = 0, long outputBytes = 0, long workingSetBytes = 0, long cpuMs = 0, ResourceSnapshot? resource = null, ConversionStageReport? stages = null, long gateWaitMs = 0, DateTimeOffset? detectedAtUtc = null, DateTimeOffset? claimedAtUtc = null, DateTimeOffset? conversionStartedAtUtc = null, DateTimeOffset? completedAtUtc = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var utc = DateTimeOffset.UtcNow;
            var wallDurationMs = checked(durationMs + gateWaitMs);
            var pickupWaitMs = DurationBetween(detectedAtUtc, claimedAtUtc);
            var queueWaitMs = DurationBetween(claimedAtUtc, conversionStartedAtUtc);
            var totalEndToEndMs = DurationBetween(detectedAtUtc, completedAtUtc ?? utc);
            var failure = exception is null ? null : ConversionFailureClassifier.Classify(exception);
            await _detailedWriter.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, runId = _runId, utc, eventName, path, success, attempt, durationMs, wallDurationMs, gateWaitMs, pickupWaitMs, queueWaitMs, totalEndToEndMs, detectedAtUtc, claimedAtUtc, conversionStartedAtUtc, completedAtUtc, inputBytes, outputBytes, workingSetBytes, cpuMs, resource, stages, errorType = exception?.GetType().Name, errorCode = failure?.Code, errorMessage = failure?.Message, retryable = failure?.IsRetryable })).ConfigureAwait(false);
            await _userWriter.WriteLineAsync(FormatUserLine(utc, eventName, path, success, attempt, durationMs, wallDurationMs, gateWaitMs, pickupWaitMs, queueWaitMs, totalEndToEndMs, inputBytes, outputBytes, failure?.Code)).ConfigureAwait(false);
            await FlushIfDueAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteResourceAsync(string path, string phase, ResourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _detailedWriter.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, runId = _runId, utc = DateTimeOffset.UtcNow, eventName = "resource", phase, path, resource = snapshot })).ConfigureAwait(false);
            await FlushIfDueAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteServiceAsync(string eventName, string message, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var utc = DateTimeOffset.UtcNow;
            await _detailedWriter.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, runId = _runId, utc, eventName, path = "service", success = true, message })).ConfigureAwait(false);
            await _userWriter.WriteLineAsync($"{utc:O} INFO {eventName} {message}").ConfigureAwait(false);
            await FlushIfDueAsync(force: true).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _detailedWriter.Flush();
            _userWriter.Flush();
        }
        finally { _gate.Release(); }
        _detailedWriter.Dispose();
        _userWriter.Dispose();
        _gate.Dispose();
    }

    private static StreamWriter CreateWriter(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous)) { AutoFlush = false };

    private async Task FlushIfDueAsync(bool force = false)
    {
        _pendingLines += 2;
        var now = DateTimeOffset.UtcNow;
        if (!force && _pendingLines < FlushBatchSize && now - _lastFlushUtc < FlushInterval) return;
        await _detailedWriter.FlushAsync().ConfigureAwait(false);
        await _userWriter.FlushAsync().ConfigureAwait(false);
        _pendingLines = 0;
        _lastFlushUtc = now;
    }

    private static string FormatUserLine(DateTimeOffset utc, string eventName, string path, bool success, int attempt, long durationMs, long wallDurationMs, long gateWaitMs, long pickupWaitMs, long queueWaitMs, long totalEndToEndMs, long inputBytes, long outputBytes, string? errorCode)
    {
        var level = eventName switch
        {
            "error" => "ERROR",
            "retry" => "WARN",
            _ when success => "INFO",
            _ => "WARN"
        };
        var reason = errorCode is null ? string.Empty : $" reason={Normalize(errorCode)}";
        return $"{utc:O} {level} {eventName} file={path} success={success} attempt={attempt} durationMs={durationMs} wallDurationMs={wallDurationMs} gateWaitMs={gateWaitMs} pickupWaitMs={pickupWaitMs} queueWaitMs={queueWaitMs} totalEndToEndMs={totalEndToEndMs} inputBytes={inputBytes} outputBytes={outputBytes}{reason}";
    }

    private static string Normalize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static long DurationBetween(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null ? 0 : Math.Max(0, (long)(end.Value - start.Value).TotalMilliseconds);
}
