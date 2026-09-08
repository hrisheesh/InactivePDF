using System.Text.Json;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.UnitTests.Api;

public sealed class ConversionAnalyticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inactivepdf-analytics-" + Guid.NewGuid().ToString("N"));

    public ConversionAnalyticsTests() => Directory.CreateDirectory(root);
    private ConversionWorkerRequest Request(string name, string source = "Synchronous") => new(ConversionOperation.ConvertFile, Path.Combine(root, "result.pdf"), [new ConversionWorkerInput(Path.Combine(root, name), name)], Source: source);

    [Fact]
    public void ComputesRealDocumentAndFormatTimingsAndExcludesFailures()
    {
        using var store = new ConversionTelemetryStore(Path.Combine(root, "analytics.db"));
        foreach (var (name, source, milliseconds) in new[] { ("one.docx", "Synchronous", 100d), ("two.docx", "Queued", 300d), ("image.png", "WatchFolder", 500d) })
        {
            var request = Request(name, source);
            store.Complete(store.Begin(request), milliseconds, request.OutputPath, null);
        }
        var failed = Request("failed.docx");
        store.Complete(store.Begin(failed), 900, failed.OutputPath, new IOException("sensitive engine stderr must not be exposed"));
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(store.Snapshot()));
        var data = snapshot.RootElement;
        Assert.Equal(4, data.GetProperty("attempts").GetInt32());
        Assert.Equal(1, data.GetProperty("failed").GetInt32());
        Assert.Equal(300, data.GetProperty("documentTiming").GetProperty("averageMs").GetDouble());
        Assert.Equal(100, data.GetProperty("documentTiming").GetProperty("minMs").GetDouble());
        Assert.Equal(500, data.GetProperty("documentTiming").GetProperty("maxMs").GetDouble());
        var docx = data.GetProperty("formats").EnumerateArray().Single(row => row.GetProperty("format").GetString() == "docx");
        Assert.Equal(200, docx.GetProperty("timing").GetProperty("averageMs").GetDouble());
        Assert.Equal(3, data.GetProperty("sources").GetArrayLength());
        Assert.DoesNotContain("sensitive engine stderr", data.GetRawText());
    }

    [Fact]
    public void KeepsHistoricalObservationsAcrossRestartAndMarksUnfinishedExecutions()
    {
        var path = Path.Combine(root, "analytics.db");
        using (var store = new ConversionTelemetryStore(path))
        {
            var request = Request("invoice.pdf");
            store.Complete(store.Begin(request), 25, request.OutputPath, null);
            store.Begin(Request("unfinished.doc"));
        }
        using var restored = new ConversionTelemetryStore(path);
        Assert.Equal("Succeeded", Assert.Single(restored.Search("invoice")).State);
        Assert.Equal("Interrupted", Assert.Single(restored.Search("unfinished")).State);
    }

    [Fact]
    public void EmptyTimingsAreNullAndBatchExecutionsAreNotClaimedAsDocumentTimings()
    {
        using var store = new ConversionTelemetryStore(Path.Combine(root, "analytics.db"));
        var request = Request("first.docx") with { Inputs = [new("first.docx", "first.docx"), new("second.png", "second.png")] };
        store.Complete(store.Begin(request), 400, request.OutputPath, null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(store.Snapshot()));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("documentTiming").GetProperty("averageMs").ValueKind);
        Assert.Equal(400, json.RootElement.GetProperty("executionTiming").GetProperty("averageMs").GetDouble());
        Assert.Equal("mixed", json.RootElement.GetProperty("formats")[0].GetProperty("format").GetString());
    }
    public void Dispose() => Directory.Delete(root, true);
}
