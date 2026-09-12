using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Watch;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

public sealed class AdministrationLiveService(
    HealthCheckService health, ConversionTelemetryStore telemetry, LiteDbJobStore jobs,
    ConversionMetrics metrics, IConversionJobBuffer queue, WatchFolderOptions watch, AdministrationSettingsStore settings,
    WatermarkProfileStore profiles, SwarmScheduler swarm, ApiKeyStore apiKeys, ApiUsageStore apiUsage, AuditEventStore audit) : IDisposable
{
    private readonly SemaphoreSlim snapshotGate = new(1, 1);
    private DateTime lastSnapshot;
    private object? cached;
    private string? cachedSource;

    public async Task<object> SnapshotAsync(CancellationToken cancellationToken, string? source = null)
    {
        await snapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && cachedSource == source && DateTime.UtcNow - lastSnapshot < TimeSpan.FromSeconds(1)) return cached;
            var report = await health.CheckHealthAsync(cancellationToken);
            var input = CountFiles(watch.InputPath);
            var processing = CountFiles(watch.ProcessingPath);
            var errors = CountFiles(watch.ErrorsPath);
            var watchActive = swarm.Counts("WatchFolder").Processing;
            var apiActive = swarm.Counts("Queued").Processing;
            var watchWaiting = Math.Max(0, (input ?? 0) + (processing ?? 0) - watchActive);
            var durableOutstanding = jobs.PendingCount;
            var apiWaiting = Math.Max(0, durableOutstanding - apiActive);
            var syncWaiting = swarm.Counts("Synchronous").Waiting;
            var waiting = source switch { "WatchFolder" => watchWaiting, "Queued" => apiWaiting, "Synchronous" => syncWaiting, _ => watchWaiting + apiWaiting + syncWaiting };
            using var process = Process.GetCurrentProcess();
            var resource = ResourceSnapshot.Capture();
            cached = new
            {
                sampledAt = DateTimeOffset.UtcNow,
                status = new
                {
                    uptimeSeconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                    processMemoryBytes = process.WorkingSet64, cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                    logicalProcessors = Environment.ProcessorCount, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    authenticationEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN")),
                    readiness = report.Status.ToString(), checks = report.Entries.Select(pair => new { name = pair.Key, status = pair.Value.Status.ToString(), description = pair.Value.Description })
                },
                resources = new
                {
                    capturedAt = resource.Utc,
                    apiWorkingSetBytes = resource.WorkingSetBytes,
                    apiPhysicalFootprintBytes = resource.PhysicalFootprintBytes,
                    apiCpuMilliseconds = resource.CpuMilliseconds,
                    apiThreadCount = resource.ThreadCount,
                    libreOfficeProcessCount = resource.LibreOfficeProcessCount,
                    libreOfficeWorkingSetBytes = resource.LibreOfficeWorkingSetBytes,
                    libreOfficeCpuMilliseconds = resource.LibreOfficeCpuMilliseconds,
                    libreOfficeThreadCount = resource.LibreOfficeThreadCount
                },
                metrics = new { metrics.Accepted, metrics.Succeeded, metrics.Failed, metrics.Retried, metrics.DeadLettered, queueDepth = waiting, queueCapacity = queue.Capacity,
                    dispatchBufferDepth = queue.Count, durableOutstanding, processing = swarm.Counts(source).Processing },
                swarm = swarm.Snapshot(source), watchFolder = new { input, processing, errors, waiting = watchWaiting, active = watchActive, statusAvailable = input.HasValue && processing.HasValue && errors.HasValue },
                apiUsage = apiUsage.SnapshotAll(apiKeys.List()), auditEvents = audit.Recent(40),
                settings = settings.Read(), profiles = profiles.List(), analytics = telemetry.Snapshot(24, source), jobs = jobs.ListRecent().Select(JobApiResponses.ToPublic),
                deadLetters = (await jobs.ListDeadLettersAsync(50, cancellationToken)).Select(row => new { row.JobId, row.ErrorCode }),
                logs = ReadLogs("")
            };
            lastSnapshot = DateTime.UtcNow;
            cachedSource = source;
            return cached;
        }
        finally { snapshotGate.Release(); }
    }

    private static int? CountFiles(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            WorkspacePathSecurity.EnsureSafeChain(path, path);
            return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Count(file => !Path.GetFileName(file).StartsWith('.') && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Read only structured operator metadata, never raw engine stderr or document contents.
    public object ReadLogs(string query)
    {
        var rows = new List<OperatorLog>();
        var unreadable = 0;
        var malformed = 0;
        var filesRead = 0;
        try
        {
            if (!Directory.Exists(watch.LogPath)) return new { rows, filesRead, unreadable, malformed, coverage = "Newest 5 JSONL logs, last 256 KiB per file" };
            var root = new DirectoryInfo(watch.LogPath);
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException();
            foreach (var file in root.EnumerateFiles("InactivePDF-*.jsonl", SearchOption.TopDirectoryOnly).OrderByDescending(file => file.Name).Take(5))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { unreadable++; continue; }
                try
                {
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var offset = Math.Max(0, stream.Length - 256 * 1024);
                    stream.Seek(offset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    if (offset > 0) reader.ReadLine();
                    while (reader.ReadLine() is { } line)
                    {
                        try
                        {
                            using var json = JsonDocument.Parse(line);
                            var data = json.RootElement;
                            string Text(string key) => data.TryGetProperty(key, out var value) ? value.ToString() : "";
                            if (Text("eventName") == "resource") continue;
                            var entry = new OperatorLog(Text("utc"), Text("eventName"), Path.GetFileName(Text("path")), Text("success"), Text("attempt"), Text("durationMs"), Text("errorType"));
                            if (query.Length == 0 || $"{entry.FileName} {entry.Event} {entry.ErrorType} {entry.Utc}".Contains(query, StringComparison.OrdinalIgnoreCase)) rows.Add(entry);
                        }
                        catch (JsonException) { malformed++; }
                    }
                    filesRead++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unreadable++; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unreadable++; }
        return new { rows = rows.OrderByDescending(row => row.Utc).Take(100).ToArray(), filesRead, unreadable, malformed, coverage = "Newest 5 JSONL logs, last 256 KiB per file. Raw engine messages are excluded." };
    }

    public sealed record OperatorLog(string Utc, string Event, string FileName, string Success, string Attempt, string DurationMs, string ErrorType);
    public void Dispose() => snapshotGate.Dispose();
}
