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
                    InputBytes = request.Inputs.Sum(input => File.Exists(input.Path) ? new FileInfo(input.Path).Length : 0), State = "Running"
                };
                row.SearchText = $"{row.Id} {row.JobId} {row.FileName} {row.Format} {row.Source} {row.Operation}".ToLowerInvariant();
                observations.Insert(row);
                RecordingError = null;
                return row;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LiteException)
            { RecordingError = "Conversion analytics could not be recorded: " + error.GetType().Name; return null; }
        }
    }

    public void Complete(ConversionObservation? row, double milliseconds, string outputPath, Exception? error)
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
            return new
            {
                since, hours, source, limited, recordingError = RecordingError, attempts = rows.Length,
                succeeded = success.Length, failed = rows.Count(x => x.State == "Failed"), cancelled = rows.Count(x => x.State == "Cancelled"),
                interrupted = rows.Count(x => x.State == "Interrupted"), running = rows.Count(x => x.State == "Running"),
                inputBytes = rows.Sum(x => x.InputBytes), outputBytes = success.Sum(x => x.OutputBytes),
                documentTiming = Timing(individual), executionTiming = Timing(success),
                formats = rows.GroupBy(x => x.Format).OrderBy(x => x.Key).Select(group => new { format = group.Key, attempts = group.Count(), timing = Timing(group.Where(x => x.State == "Succeeded" && x.DurationMs.HasValue).ToArray()) }).ToArray(),
                sources = rows.GroupBy(x => x.Source).Select(group => new { source = group.Key, attempts = group.Count(), succeeded = group.Count(x => x.State == "Succeeded") }).ToArray(),
                timeline = rows.GroupBy(x => new DateTime(x.StartedAt.Year, x.StartedAt.Month, x.StartedAt.Day, x.StartedAt.Hour, 0, 0, DateTimeKind.Utc)).OrderBy(x => x.Key)
                    .Select(group => new { utc = group.Key, succeeded = group.Count(x => x.State == "Succeeded"), failed = group.Count(x => x.State == "Failed") }).ToArray(),
                recent = rows.Take(30).ToArray()
            };
        }
    }

    private static object Timing(ConversionObservation[] rows) => new
    {
        count = rows.Length,
        averageMs = rows.Length == 0 ? (double?)null : rows.Average(x => x.DurationMs!.Value),
        minMs = rows.Length == 0 ? (double?)null : rows.Min(x => x.DurationMs!.Value),
        maxMs = rows.Length == 0 ? (double?)null : rows.Max(x => x.DurationMs!.Value)
    };

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
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? DurationMs { get; set; }
    public double QueueWaitMs { get; set; }
    public string? WorkerId { get; set; }
    public string State { get; set; } = "";
    public string? ErrorCode { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public string SearchText { get; set; } = "";
}
