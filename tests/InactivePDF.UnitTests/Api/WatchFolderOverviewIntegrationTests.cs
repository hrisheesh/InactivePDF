using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class WatchFolderOverviewIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task WatchFolderConversionProducesPdfAndAppearsInOverviewAnalytics()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var names = new[] { "INACTIVEPDF_DATA_PATH", "INACTIVEPDF_STATE_PATH", "INACTIVEPDF_JOBS_PATH", "INACTIVEPDF_WATCH_ROOT", "INACTIVEPDF_API_TOKEN" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_STATE_PATH", Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_JOBS_PATH", Path.Combine(root, "jobs"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_ROOT", Path.Combine(root, "watch"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", "watch-test-token");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "watch-test-token");
            var input = Path.Combine(root, "watch", "Input", "watch-overview.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            await File.WriteAllTextAsync(input, "This document was processed by the watch folder.");

            var output = Path.Combine(root, "watch", "Output", "watch-overview.pdf");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(output))
            {
                await Task.Delay(250, deadline.Token);
            }

            Assert.True(new FileInfo(output).Length > 500);
            Assert.True(File.Exists(Path.Combine(root, "watch", "Originals", "watch-overview.txt")));
            var analytics = await client.GetFromJsonAsync<JsonObject>("/v1/admin/analytics?hours=1", deadline.Token);
            Assert.NotNull(analytics);
            Assert.True(analytics["succeeded"]!.GetValue<int>() >= 1);
            Assert.Contains(analytics["sources"]!.AsArray(), row => row!["source"]!.GetValue<string>() == "WatchFolder");
            Assert.Contains(analytics["formats"]!.AsArray(), row => row!["format"]!.GetValue<string>() == "txt");
            var search = await client.GetFromJsonAsync<JsonObject>("/v1/admin/search?q=watch-overview", deadline.Token);
            Assert.Contains(search!["executions"]!.AsArray(), row => row!["source"]!.GetValue<string>() == "WatchFolder");
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
