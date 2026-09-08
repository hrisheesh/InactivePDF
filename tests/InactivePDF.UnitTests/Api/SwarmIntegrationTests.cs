using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class SwarmIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RealWatchAndApiBurstsProduceEveryOutputAndLiveSourceMetrics(int workers)
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-swarm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsServiceVariable).ToDictionary(x => x, Environment.GetEnvironmentVariable);
        foreach (var name in previous.Keys) Environment.SetEnvironmentVariable(name, null);
        var settings = new InactivePdfSettings();
        settings.Paths.DataRoot = root;settings.Paths.StateRoot = Path.Combine(root, "state");settings.Paths.JobRoot = Path.Combine(root, "jobs");settings.Paths.WatchRoot = Path.Combine(root, "watch");
        settings.Performance.MaximumParallelWorkers = workers;settings.Performance.Profile = workers == 1 ? "Conservative" : "Swarm";
        settings.Workers.Count = workers;settings.WatchFolder.MaximumConcurrentConversions = workers;
        settings.Concurrency.Text = workers;settings.Concurrency.Pdf = workers;
        settings.Resources.MinimumFreeDiskBytes = 1;
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));
        Environment.SetEnvironmentVariable("INACTIVEPDF_SETTINGS_PATH", path);
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", "swarm-test");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "swarm-test");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var seed = await client.PostAsJsonAsync("/v1/create-text-pdf", new { text = "Swarm PDF integrity fixture", profile = "archive" }, deadline.Token);
            seed.EnsureSuccessStatusCode();var pdf = await seed.Content.ReadAsByteArrayAsync(deadline.Token);
            var watchRoot = Path.Combine(root, "watch");
            var originals = new Dictionary<string, byte[]>();
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                // Two source types deliberately share a base name: output allocation must not overwrite.
                originals.Add($"document-{i:D2}.txt", System.Text.Encoding.UTF8.GetBytes("Unique original " + i));
                originals.Add($"document-{i:D2}.pdf", pdf);
            }
            foreach (var (name, bytes) in originals) await File.WriteAllBytesAsync(Path.Combine(watchRoot, "Input", name), bytes, deadline.Token);
            var early = await Sample(client, "WatchFolder", deadline.Token);
            Assert.True(early["metrics"]!["queueDepth"]!.GetValue<int>() > 0);
            var synchronous = Enumerable.Range(0, 20).Select(async i =>
            {
                using var response = await client.PostAsJsonAsync("/v1/create-text-pdf", new { text = "Parallel API document " + i, profile = "archive" }, deadline.Token);
                response.EnsureSuccessStatusCode();
                using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                Assert.True(document.PageCount > 0);
            }).ToArray();
            var jobIds = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                using var form = new MultipartFormDataContent { { new StringContent("CreateTextPdf"), "operation" }, { new StringContent("Durable swarm job " + i), "text" } };
                using var accepted = await client.PostAsync("/v1/jobs", form, deadline.Token);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                var job = await accepted.Content.ReadFromJsonAsync<JsonObject>(deadline.Token);
                jobIds.Add(job!["jobId"]!.GetValue<string>());
            }
            await Task.WhenAll(synchronous);
            JsonObject finished;
            do
            {
                await Task.Delay(100, deadline.Token);
                finished = await Sample(client, "WatchFolder", deadline.Token);
            } while (finished["analytics"]!["succeeded"]!.GetValue<int>() < 20 || Directory.EnumerateFiles(Path.Combine(watchRoot, "Originals")).Count() < 20);
            Assert.Equal(20, finished["analytics"]!["succeeded"]!.GetValue<int>());
            Assert.Equal(0, finished["metrics"]!["queueDepth"]!.GetValue<int>());
            Assert.Equal("WatchFolder", finished["analytics"]!["source"]!.GetValue<string>());
            Assert.Equal(20, Directory.GetFiles(Path.Combine(watchRoot, "Output"), "*.pdf").Length);
            foreach (var (name, original) in originals) Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(watchRoot, "Originals", name), deadline.Token));
            foreach (var file in Directory.GetFiles(Path.Combine(watchRoot, "Output"), "*.pdf"))
            { using var document = PdfReader.Open(file, PdfDocumentOpenMode.Import);Assert.True(document.PageCount > 0); }
            foreach (var id in jobIds)
            {
                JsonObject? status;
                do { status = await client.GetFromJsonAsync<JsonObject>("/v1/jobs/" + id, deadline.Token);if(status!["state"]!.ToString() != "Succeeded")await Task.Delay(100, deadline.Token); } while(status!["state"]!.ToString() != "Succeeded");
                using var downloaded = await client.GetAsync("/v1/jobs/" + id + "/output", deadline.Token);downloaded.EnsureSuccessStatusCode();
            }
            var apiSample = await Sample(client, "Synchronous", deadline.Token);
            Assert.Equal("Synchronous", apiSample["analytics"]!["source"]!.GetValue<string>());
            Assert.Equal(21, apiSample["analytics"]!["succeeded"]!.GetValue<int>());
            var all = await Sample(client, "", deadline.Token);
            Assert.Equal(46, all["analytics"]!["succeeded"]!.GetValue<int>());
            Assert.InRange(all["swarm"]!["peakActive"]!.GetValue<int>(), workers == 1 ? 1 : 2, workers);
            var jobs = await client.GetFromJsonAsync<JsonArray>("/v1/admin/jobs?limit=100", deadline.Token);
            Assert.Equal(20, jobs!.Count(row => row!["correlationId"]!.ToString().StartsWith("watch:", StringComparison.Ordinal) && row["state"]!.ToString() == "Succeeded"));
            // Invalid source is preserved in Errors and receives a persistent terminal status.
            await File.WriteAllTextAsync(Path.Combine(watchRoot, "Input", "invalid.pdf"), "not a PDF", deadline.Token);
            while(!File.Exists(Path.Combine(watchRoot, "Errors", "invalid.pdf")))await Task.Delay(100, deadline.Token);
            var failedSearch = await client.GetFromJsonAsync<JsonObject>("/v1/admin/search?q=invalid.pdf", deadline.Token);
            Assert.Contains(failedSearch!["jobs"]!.AsArray(), row => row!["state"]!.ToString() == "Failed");
            output.WriteLine($"Real workload: {workers} workers, 20 watch files + 25 API documents, {timer.Elapsed.TotalSeconds:F2}s, peak {all["swarm"]!["peakActive"]}, all outputs validated.");
        }
        finally
        {
            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsServiceVariable).ToArray()) Environment.SetEnvironmentVariable(name, null);
            foreach(var (name,value) in previous)Environment.SetEnvironmentVariable(name,value);
            Directory.Delete(root, true);
        }
    }

    private static bool IsServiceVariable(string name) => name.StartsWith("INACTIVEPDF_", StringComparison.Ordinal) || name == "ASPNETCORE_URLS";

    private static async Task<JsonObject> Sample(HttpClient client, string source, CancellationToken token)
    {
        using var response = await client.GetAsync("/v1/admin/live" + (source.Length > 0 ? "?source=" + source : ""), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (await reader.ReadLineAsync(token) is { } line) if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonNode.Parse(line[6..])!.AsObject();
        throw new IOException("Live feed closed without a sample.");
    }
}
