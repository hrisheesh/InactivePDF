using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ImageMagick;
using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Rendering;
using PdfSharp.Pdf;

var durationMinutes = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinutes) ? parsedMinutes : 15;
var workerCount = args.Length > 1 && int.TryParse(args[1], out var parsedWorkers) ? parsedWorkers : Math.Max(2, Environment.ProcessorCount / 2);
var root = Path.Combine(Path.GetTempPath(), "InactivePDF-stress", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
var fixtures = Path.Combine(root, "fixtures");
var outputs = Path.Combine(root, "outputs");
var logPath = Path.Combine(root, "stress.jsonl");
Directory.CreateDirectory(fixtures);
Directory.CreateDirectory(outputs);
CreateFixtures(fixtures);

await using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous));
var counts = new long[4];
var failures = 0L;
var latencies = new List<long>();
var latencyGate = new object();
var logGate = new SemaphoreSlim(1, 1);
var stopwatch = Stopwatch.StartNew();
var deadline = TimeSpan.FromMinutes(durationMinutes);
var tasks = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
{
    var iteration = 0;
    while (stopwatch.Elapsed < deadline)
    {
        var operation = iteration++ % 4;
        var started = Stopwatch.GetTimestamp();
        var success = true;
        string? error = null;
        try
        {
            var output = Path.Combine(outputs, $"w{worker}-i{iteration}-{Guid.NewGuid():N}.pdf");
            switch (operation)
            {
                case 0:
                    new MagickToPdfConverter().Convert([Path.Combine(fixtures, "sample.png"), Path.Combine(fixtures, "sample.jpg")], output);
                    Interlocked.Increment(ref counts[0]);
                    break;
                case 1:
                    new PdfTextGenerator().Create(File.ReadAllText(Path.Combine(fixtures, "large.txt")), output);
                    Interlocked.Increment(ref counts[1]);
                    break;
                case 2:
                    new PdfSharpOperations().Merge([Path.Combine(fixtures, "sample.pdf"), Path.Combine(fixtures, "sample.pdf")], output);
                    Interlocked.Increment(ref counts[2]);
                    break;
                default:
                    _ = new PdfSharpOperations().Inspect(Path.Combine(fixtures, "sample.pdf"));
                    Interlocked.Increment(ref counts[3]);
                    break;
            }
            if (File.Exists(output)) File.Delete(output);
        }
        catch (Exception exception)
        {
            success = false;
            error = exception.ToString();
            Interlocked.Increment(ref failures);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (latencyGate) latencies.Add((long)Math.Ceiling(elapsedMs));
        await logGate.WaitAsync();
        try
        {
            await log.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, worker, operation, success, elapsedMs, error }));
        }
        finally
        {
            logGate.Release();
        }
    }
}, CancellationToken.None)).ToArray();

await Task.WhenAll(tasks);
stopwatch.Stop();
latencies.Sort();
var total = counts.Sum();
var summary = new
{
    startedUtc = DateTime.UtcNow - stopwatch.Elapsed,
    durationSeconds = stopwatch.Elapsed.TotalSeconds,
    workers = workerCount,
    operations = new { image = counts[0], text = counts[1], merge = counts[2], inspect = counts[3] },
    totalOperations = total,
    failures,
    operationsPerSecond = total / stopwatch.Elapsed.TotalSeconds,
    latencyMs = new { p50 = Percentile(latencies, .50), p95 = Percentile(latencies, .95), p99 = Percentile(latencies, .99), max = latencies.Count == 0 ? 0 : latencies[^1] },
    logPath,
    root
};
await log.WriteLineAsync(JsonSerializer.Serialize(new { summary }));
await log.FlushAsync();
Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

static long Percentile(IReadOnlyList<long> values, double percentile) => values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * percentile) - 1)];

static void CreateFixtures(string root)
{
    using (var image = new MagickImage(MagickColors.CornflowerBlue, 1600, 1200)) { image.Format = MagickFormat.Png; image.Write(Path.Combine(root, "sample.png")); }
    using (var image = new MagickImage(MagickColors.OrangeRed, 1600, 1200)) { image.Format = MagickFormat.Jpeg; image.Write(Path.Combine(root, "sample.jpg")); }
    using (var pdf = new PdfDocument()) { for (var page = 0; page < 5; page++) pdf.AddPage(); pdf.Save(Path.Combine(root, "sample.pdf")); }
    File.WriteAllText(Path.Combine(root, "large.txt"), string.Join(Environment.NewLine, Enumerable.Repeat("InactivePDF deterministic text stress fixture with Unicode: café — नमस्ते — שלום.", 5000)));
}
