using System.Diagnostics;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Infrastructure.Jobs;

public sealed record SwarmOptions(int MaximumParallelWorkers = 2, long MemoryBudgetBytes = 2L * 1024 * 1024 * 1024,
    int MaximumPending = 4096, int AgingSeconds = 30, int OfficeSlots = 1, int ImageSlots = 1, int PdfSlots = 2, int TextSlots = 2)
{
    public static SwarmOptions FromEnvironment() => new(
        Number("INACTIVEPDF_SWARM_WORKERS", 2), Bytes("INACTIVEPDF_SWARM_MEMORY_BYTES", 2L * 1024 * 1024 * 1024),
        Number("INACTIVEPDF_SWARM_PENDING", 4096), Number("INACTIVEPDF_SWARM_AGING_SECONDS", 30),
        Number("INACTIVEPDF_OFFICE_CONCURRENCY", 1), Number("INACTIVEPDF_IMAGE_CONCURRENCY", 1),
        Number("INACTIVEPDF_PDF_CONCURRENCY", 2), Number("INACTIVEPDF_TEXT_CONCURRENCY", 2));
    private static int Number(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var n) && n > 0 ? n : fallback;
    private static long Bytes(string name, long fallback) => long.TryParse(Environment.GetEnvironmentVariable(name), out var n) && n > 0 ? n : fallback;
}

/// <summary>One resource admission boundary for all conversion entry points on this server.
/// Shortest estimated jobs run first until aging reserves admission for the oldest job.
/// Selection scans a bounded queue, allowing other engine lanes to use otherwise idle slots.</summary>
public sealed class SwarmScheduler
{
    private static readonly string[] LaneNames = ["Text", "Pdf", "Image", "Office"];
    private readonly object gate = new();
    private readonly SwarmOptions options;
    private readonly List<Entry> pending = [];
    private readonly Dictionary<string, Entry> active = [];
    private readonly Dictionary<string, double> estimates = [];
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private long reservedBytes, completed, failed, cancelled;
    private int peakActive;

    public SwarmScheduler(SwarmOptions options)
    {
        if (options.MaximumParallelWorkers is < 1 or > 256 || options.MaximumPending is < 1 or > 100_000 || options.MemoryBudgetBytes < 1 || options.AgingSeconds < 1 || options.OfficeSlots < 1 || options.ImageSlots < 1 || options.PdfSlots < 1 || options.TextSlots < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
    }

    public async Task<Lease> AcquireAsync(ConversionWorkerRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lane = Lane(request);
        var inputBytes = request.Inputs.Sum(input => File.Exists(input.Path) ? new FileInfo(input.Path).Length : 0);
        var reservation = lane switch { "Office" => 1207959552L, "Image" => 1073741824L, "Pdf" => 268435456L, _ => 67108864L };
        if (reservation > options.MemoryBudgetBytes) throw new InvalidOperationException("The swarm memory budget cannot admit this document's engine. Increase Performance.MemoryBudgetBytes.");
        var entry = new Entry(Guid.NewGuid().ToString("N"), request.JobId, request.Source, string.Join(", ", request.Inputs.Select(x => Path.GetFileName(x.FileName))), lane, reservation, inputBytes);
        lock (gate)
        {
            if (pending.Count >= options.MaximumPending) throw new SwarmCapacityException();
            pending.Add(entry);
            Dispatch();
        }
        using var registration = cancellationToken.Register(() => Cancel(entry, cancellationToken));
        return await entry.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(Entry entry, CancellationToken token)
    {
        lock (gate)
        {
            if (!pending.Remove(entry)) return;
            cancelled++;
            entry.Completion.TrySetCanceled(token);
            Dispatch();
        }
    }

    private void Dispatch()
    {
        while (active.Count < options.MaximumParallelWorkers && pending.Count > 0)
        {
            var oldest = pending[0];
            var aged = Stopwatch.GetElapsedTime(oldest.Queued).TotalSeconds >= options.AgingSeconds;
            // An aged job reserves its memory; newer work cannot perpetually starve a large document.
            var candidate = aged ? (Fits(oldest) ? oldest : null) : pending.Where(Fits).MinBy(Estimate);
            if (candidate is null) return;
            pending.Remove(candidate);
            candidate.Started = Stopwatch.GetTimestamp();
            active.Add(candidate.Id, candidate);
            reservedBytes += candidate.Reservation;
            peakActive = Math.Max(peakActive, active.Count);
            candidate.Completion.SetResult(new Lease(this, candidate.Id, Stopwatch.GetElapsedTime(candidate.Queued).TotalMilliseconds));
        }
    }

    private bool Fits(Entry entry) => reservedBytes <= options.MemoryBudgetBytes - entry.Reservation && active.Values.Count(x => x.Lane == entry.Lane) < Slots(entry.Lane);
    private int Slots(string lane) => lane switch { "Office" => options.OfficeSlots, "Image" => options.ImageSlots, "Pdf" => options.PdfSlots, _ => options.TextSlots };
    private double Estimate(Entry entry) => estimates.GetValueOrDefault(entry.Lane, entry.Lane == "Office" ? 5000 : 250) * (1 + Math.Log2(1 + entry.InputBytes / 1048576d));

    private void Release(string id, Exception? error)
    {
        lock (gate)
        {
            if (!active.Remove(id, out var entry)) return;
            reservedBytes -= entry.Reservation;
            if (error is OperationCanceledException) cancelled++; else if (error is not null) failed++; else completed++;
            if (error is null)
            {
                var normalized = Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds / (1 + Math.Log2(1 + entry.InputBytes / 1048576d));
                estimates[entry.Lane] = estimates.TryGetValue(entry.Lane, out var old) ? .8 * old + .2 * normalized : normalized;
            }
            Dispatch();
        }
    }

    public object Snapshot(string? source = null)
    {
        lock (gate)
        {
            var running = active.Values.Where(x => string.IsNullOrEmpty(source) || x.Source == source).ToArray();
            var waiting = pending.Where(x => string.IsNullOrEmpty(source) || x.Source == source).ToArray();
            return new { waiting = waiting.Length, processing = running.Length, globalProcessing = active.Count,
                capacity = options.MaximumParallelWorkers, available = options.MaximumParallelWorkers - active.Count,
                peakActive, reservedBytes, memoryBudgetBytes = options.MemoryBudgetBytes, maximumPending = options.MaximumPending,
                completed, failed, cancelled, throughputPerMinute = completed / Math.Max(1, lifetime.Elapsed.TotalMinutes),
                lanes = LaneNames.Select(lane => new { name = lane, capacity = Slots(lane), processing = running.Count(x => x.Lane == lane), waiting = waiting.Count(x => x.Lane == lane) }).ToArray(),
                workers = running.Select(x => new { x.Id, x.JobId, x.Source, x.FileName, x.Lane, elapsedMs = Stopwatch.GetElapsedTime(x.Started).TotalMilliseconds }).ToArray() };
        }
    }

    public (int Waiting, int Processing) Counts(string? source = null)
    {
        lock (gate) return (pending.Count(x => string.IsNullOrEmpty(source) || x.Source == source), active.Values.Count(x => string.IsNullOrEmpty(source) || x.Source == source));
    }

    private static string Lane(ConversionWorkerRequest request)
    {
        var lanes = request.Inputs.Select(x => SupportedFormatCatalog.TryGet(Path.GetExtension(x.FileName), out var f) ? f.Route switch
        { ConversionFormatRoute.Image => "Image", ConversionFormatRoute.PdfPassThrough => "Pdf", ConversionFormatRoute.TextGenerator => "Text", _ => "Office" } : "Office").ToArray();
        return lanes.Contains("Office", StringComparer.Ordinal) ? "Office" : lanes.Contains("Image", StringComparer.Ordinal) ? "Image" : lanes.Contains("Pdf", StringComparer.Ordinal) ? "Pdf" : "Text";
    }

    private sealed class Entry(string id, string? jobId, string source, string fileName, string lane, long reservation, long inputBytes)
    {
        public string Id { get; } = id;
        public string? JobId { get; } = jobId;
        public string Source { get; } = source;
        public string FileName { get; } = fileName;
        public string Lane { get; } = lane;
        public long Reservation { get; } = reservation;
        public long InputBytes { get; } = inputBytes;
        public long Queued { get; } = Stopwatch.GetTimestamp();
        public long Started { get; set; }
        public TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Lease(SwarmScheduler owner, string id, double waitMs) : IDisposable
    {
        private int released;
        public string WorkerId => id;
        public double WaitMs => waitMs;
        public Exception? Error { get; set; }
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(id, Error); }
    }
}

public sealed class SwarmCapacityException : IOException
{
    public SwarmCapacityException() : base("Swarm admission is full. Retry after current conversions complete.") { }
}
