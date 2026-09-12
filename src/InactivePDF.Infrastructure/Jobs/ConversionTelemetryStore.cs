using System.Diagnostics;
using InactivePDF.Infrastructure.Processes;
using LiteDB;

namespace InactivePDF.Infrastructure.Jobs;

/// <summary>Durable execution observations shared by synchronous, queued, and watch-folder conversions.</summary>
public sealed class ConversionTelemetryStore : IDisposable
{
    private readonly LiteDatabase database;
    private readonly ILiteCollection<ConversionObservation> observations;
    private readonly object gate = new();
    public string? RecordingError { get; private set; }

    public ConversionTelemetryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        database = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Shared });
        observations = database.GetCollection<ConversionObservation>("executions");
        observations.EnsureIndex(x => x.StartedAt);
        observations.EnsureIndex(x => x.SearchText);
        foreach (var row in observations.Find(x => x.State == "Running").ToArray())
        {
            row.State = "Interrupted";
            row.ErrorCode = "service_restarted_before_observation_completed";
            observations.Update(row);
        }
    }

    public ConversionObservation? Begin(ConversionWorkerRequest request)
    {
        lock (gate)
        {
            try
            {
                var formats = request.Inputs.Select(input => Path.GetExtension(input.FileName).TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
                var row = new ConversionObservation
                {
                    Id = Guid.NewGuid().ToString("N"), JobId = request.JobId, Source = request.Source,
                    FileName = string.Join(", ", request.Inputs.Select(input => Path.GetFileName(input.FileName))),
                    Format = formats.Length == 1 ? formats[0] : "mixed", InputCount = request.Inputs.Count,
                    Operation = request.Operation.ToString(), Profile = request.Profile, StartedAt = DateTime.UtcNow,
                    ExecutionMode = request.ExecutionMode.ToString(),
                    InputBytes = request.Inputs.Sum(input => File.Exists(input.Path) ? new FileInfo(input.Path).Length : 0), State = "Running"
                };
                row.SearchText = $"{row.Id} {row.JobId} {row.FileName} {row.Format} {row.Source} {row.Operation} {row.ExecutionMode}".ToLowerInvariant();
                observations.Insert(row);
                RecordingError = null;
                return row;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LiteException)
            { RecordingError = "Conversion analytics could not be recorded: " + error.GetType().Name; return null; }
        }
    }

    public void Complete(ConversionObservation? row, double milliseconds, string outputPath, Exception? error, IsolatedWorkerMetrics? metrics = null)
    {
        if (row is null) return;
        lock (gate)
        {
            try
            {
                row.CompletedAt = DateTime.UtcNow;
                row.DurationMs = Math.Max(0, milliseconds);
                row.State = error is null ? "Succeeded" : error is OperationCanceledException ? "Cancelled" : "Failed";
                row.ErrorCode = error?.GetType().Name;
                row.OutputBytes = error is null && File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                if (metrics is not null)
                {
                    row.WorkerProcessId = metrics.ProcessId;
                    row.WorkerPeakWorkingSetBytes = metrics.PeakWorkingSetBytes;
                    row.WorkerPeakPhysicalFootprintBytes = metrics.PeakPhysicalFootprintBytes;
                    row.WorkerPeakThreadCount = metrics.PeakThreadCount;
                    row.WorkerCpuMilliseconds = metrics.CpuMilliseconds;
                    row.PeakProcessTreeMemoryBytes = metrics.PeakProcessTreeMemoryBytes;
                    row.ProcessTreeCpuMilliseconds = metrics.ProcessTreeCpuMilliseconds;
                    row.PeakProcessCount = metrics.PeakProcessCount;
                    row.Stages = metrics.Stages?.Stages.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                }
                row.SearchText += " " + row.State.ToLowerInvariant() + " " + row.ErrorCode?.ToLowerInvariant();
                observations.Update(row);
                RecordingError = null;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or LiteException)
            { RecordingError = "Conversion analytics could not be recorded: " + failure.GetType().Name; }
        }
    }

    public IReadOnlyList<ConversionObservation> Search(string query, int limit = 30)
    {
        lock (gate)
        {
            var text = query.Trim().ToLowerInvariant();
            return text.Length == 0 ? observations.Query().OrderByDescending(x => x.StartedAt).Limit(Math.Clamp(limit, 1, 100)).ToArray()
                : observations.Find(x => x.SearchText.Contains(text), limit: Math.Clamp(limit, 1, 100)).ToArray();
        }
    }

    public ConversionObservation? LatestForJob(Guid jobId)
    {
        lock (gate)
            return observations.Query().Where(row => row.JobId == jobId.ToString()).OrderByDescending(row => row.StartedAt).FirstOrDefault();
    }

    public object Snapshot(int hours = 24, string? source = null)
    {
        lock (gate)
        {
            var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours, 1, 168));
            var query = observations.Query().Where(x => x.StartedAt >= since);
            if (!string.IsNullOrEmpty(source)) query = query.Where(x => x.Source == source);
            var rows = query.OrderByDescending(x => x.StartedAt).Limit(100001).ToArray();
            var limited = rows.Length > 100000;
            if (limited) rows = rows.Take(100000).ToArray();
            var success = rows.Where(x => x.State == "Succeeded" && x.DurationMs.HasValue).ToArray();
            var individual = success.Where(x => x.InputCount == 1).ToArray();
            var retryCount = rows.Where(x => !string.IsNullOrWhiteSpace(x.JobId)).GroupBy(x => x.JobId, StringComparer.Ordinal).Sum(group => Math.Max(0, group.Count() - 1));
            var outputBytes = success.Sum(x => x.OutputBytes);
            var inputBytes = rows.Sum(x => x.InputBytes);
            var successfulInputBytes = success.Sum(x => x.InputBytes);
            return new
            {
                since, hours, source, limited, recordingError = RecordingError, attempts = rows.Length,
                succeeded = success.Length, failed = rows.Count(x => x.State == "Failed"), cancelled = rows.Count(x => x.State == "Cancelled"),
                interrupted = rows.Count(x => x.State == "Interrupted"), running = rows.Count(x => x.State == "Running"),
                inputBytes, successfulInputBytes, outputBytes, bytesSaved = successfulInputBytes - outputBytes,
                savingsPercent = successfulInputBytes == 0 ? (double?)null : (successfulInputBytes - outputBytes) * 100d / successfulInputBytes,
                retryCount, retryRate = rows.Length == 0 ? (double?)null : retryCount * 100d / rows.Length,
                documentTiming = Timing(individual), executionTiming = Timing(success), queueWaitTiming = Timing(success, row => row.QueueWaitMs),
                processAccounting = ProcessAccounting(success),
                stageTiming = StageTiming(success),
                formats = rows.GroupBy(x => x.Format).OrderBy(x => x.Key).Select(group =>
                {
                    var successful = group.Where(x => x.State == "Succeeded" && x.DurationMs.HasValue).ToArray();
                    var inputBytes = successful.Sum(x => x.InputBytes);
                    var outputBytes = successful.Sum(x => x.OutputBytes);
                    return new
                    {
                        format = group.Key,
                        attempts = group.Count(),
                        timing = Timing(successful),
                        inputBytes,
                        outputBytes,
                        bytesSaved = inputBytes - outputBytes,
                        savingsPercent = inputBytes == 0 ? (double?)null : (inputBytes - outputBytes) * 100d / inputBytes
                    };
                }).ToArray(),
                sources = rows.GroupBy(x => x.Source).Select(group => new { source = group.Key, attempts = group.Count(), succeeded = group.Count(x => x.State == "Succeeded") }).ToArray(),
                timeline = rows.GroupBy(x => new DateTime(x.StartedAt.Year, x.StartedAt.Month, x.StartedAt.Day, x.StartedAt.Hour, 0, 0, DateTimeKind.Utc)).OrderBy(x => x.Key)
                    .Select(group => new { utc = group.Key, succeeded = group.Count(x => x.State == "Succeeded"), failed = group.Count(x => x.State == "Failed") }).ToArray(),
                recent = rows.Take(30).ToArray()
            };
        }
    }

    private static object Timing(ConversionObservation[] rows) => Timing(rows, row => row.DurationMs!.Value);

    private static object Timing(ConversionObservation[] rows, Func<ConversionObservation, double> selector) => new
    {
        count = rows.Length,
        averageMs = rows.Length == 0 ? (double?)null : rows.Average(selector),
        minMs = rows.Length == 0 ? (double?)null : rows.Min(selector),
        maxMs = rows.Length == 0 ? (double?)null : rows.Max(selector)
    };

    private static object ProcessAccounting(ConversionObservation[] rows)
    {
        var observed = rows.Where(row => row.WorkerCpuMilliseconds > 0 || row.PeakProcessTreeMemoryBytes > 0).ToArray();
        return new
        {
            count = observed.Length,
            peakWorkerWorkingSetBytes = observed.Length == 0 ? 0 : observed.Max(row => row.WorkerPeakWorkingSetBytes),
            peakWorkerPhysicalFootprintBytes = observed.Length == 0 ? 0 : observed.Max(row => row.WorkerPeakPhysicalFootprintBytes),
            peakProcessTreeMemoryBytes = observed.Length == 0 ? 0 : observed.Max(row => row.PeakProcessTreeMemoryBytes),
            peakProcessCount = observed.Length == 0 ? 0 : observed.Max(row => row.PeakProcessCount),
            totalWorkerCpuMilliseconds = observed.Sum(row => row.WorkerCpuMilliseconds),
            totalProcessTreeCpuMilliseconds = observed.Sum(row => row.ProcessTreeCpuMilliseconds)
        };
    }

    private static StageTimingSummary[] StageTiming(ConversionObservation[] rows) => rows
        .SelectMany(row => row.Stages ?? new Dictionary<string, ConversionStageTiming>())
        .GroupBy(pair => pair.Key, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new StageTimingSummary(
            group.Key,
            group.Count(),
            group.Average(pair => pair.Value.WallMilliseconds),
            group.Min(pair => pair.Value.WallMilliseconds),
            group.Max(pair => pair.Value.WallMilliseconds),
            group.Sum(pair => pair.Value.CpuMilliseconds)))
        .ToArray();

    private sealed record StageTimingSummary(
        string Stage,
        int Count,
        double AverageMs,
        double MinMs,
        double MaxMs,
        double CpuMilliseconds);

    public void Dispose() => database.Dispose();
}

public sealed class ConversionObservation
{
    public string Id { get; set; } = "";
    public string? JobId { get; set; }
    public string Source { get; set; } = "Synchronous";
    public string FileName { get; set; } = "";
    public string Format { get; set; } = "";
    public int InputCount { get; set; }
    public string Operation { get; set; } = "";
    public string Profile { get; set; } = "";
    public string ExecutionMode { get; set; } = "Production";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? DurationMs { get; set; }
    public double QueueWaitMs { get; set; }
    public string? WorkerId { get; set; }
    public int WorkerProcessId { get; set; }
    public long WorkerPeakWorkingSetBytes { get; set; }
    public long WorkerPeakPhysicalFootprintBytes { get; set; }
    public int WorkerPeakThreadCount { get; set; }
    public long WorkerCpuMilliseconds { get; set; }
    public long PeakProcessTreeMemoryBytes { get; set; }
    public long ProcessTreeCpuMilliseconds { get; set; }
    public int PeakProcessCount { get; set; }
    public Dictionary<string, ConversionStageTiming>? Stages { get; set; }
    public string State { get; set; } = "";
    public string? ErrorCode { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public string SearchText { get; set; } = "";
}
