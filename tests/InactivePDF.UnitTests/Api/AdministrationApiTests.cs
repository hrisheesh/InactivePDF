using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class AdministrationApiTests
{
    [Fact]
    public async Task ServesConsoleAssetsAndProtectsSettingsWithBearerAuthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-console-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var variables = new[] { "INACTIVEPDF_SETTINGS_PATH", "INACTIVEPDF_DATA_PATH", "INACTIVEPDF_STATE_PATH", "INACTIVEPDF_JOBS_PATH", "INACTIVEPDF_WATERMARK_ASSET_PATH", "INACTIVEPDF_API_TOKEN", "INACTIVEPDF_WATCH_ROOT" };
        var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new InactivePdfSettings()));
        Environment.SetEnvironmentVariable("INACTIVEPDF_SETTINGS_PATH", path);
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_STATE_PATH", Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_JOBS_PATH", Path.Combine(root, "jobs"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_WATERMARK_ASSET_PATH", Path.Combine(root, "assets"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_ROOT", Path.Combine(root, "watch"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", "console-test-token");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            foreach (var (url, mime, marker) in new[] { ("/", "text/html", "console.js"), ("/console.js", "text/javascript", "renderOverview"), ("/console.css", "text/css", ".sidebar") })
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(mime, response.Content.Headers.ContentType!.MediaType);
                Assert.Contains(marker, await response.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/settings")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/status")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "console-test-token");
            var snapshot = await client.GetFromJsonAsync<JsonObject>("/v1/admin/settings");
            Assert.NotNull(snapshot);
            Assert.DoesNotContain("console-test-token", snapshot.ToJsonString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/performance-presets")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/admin/analytics?source=unknown")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/v1/admin/service-profiles/incomplete", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/v1/admin/service-profiles/invalid.name", snapshot["settings"])).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/v1/admin/service-profiles/premium", snapshot["settings"])).StatusCode);
            var profile = await client.GetFromJsonAsync<JsonObject>("/v1/admin/service-profiles/premium");
            Assert.True(JsonNode.DeepEquals(snapshot["settings"], profile));
            Assert.Contains("premium", (await client.GetFromJsonAsync<string[]>("/v1/admin/service-profiles"))!);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/v1/admin/service-profiles/premium")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/admin/service-profiles/premium")).StatusCode);
            snapshot["settings"]!["Workers"]!["Count"] = 2;
            var request = new { settings = snapshot["settings"], revision = snapshot["revision"]!.GetValue<string>() };
            var saved = await client.PutAsJsonAsync("/v1/admin/settings", request);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(2, InactivePdfSettings.Load(path).Workers.Count);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/v1/admin/settings", request)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/status")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/jobs")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/search?q=example")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/analytics")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/logs")).StatusCode);
            using var conversion = await client.PostAsJsonAsync("/v1/create-text-pdf", new { text = "Live dashboard integration verification", profile = "archive" });
            Assert.True(conversion.IsSuccessStatusCode, await conversion.Content.ReadAsStringAsync());
            var analytics = await client.GetFromJsonAsync<JsonObject>("/v1/admin/analytics");
            Assert.True(analytics!["succeeded"]!.GetValue<int>() >= 1);
            Assert.True(analytics["executionTiming"]!["averageMs"]!.GetValue<double>() > 0);
            var executionSearch = await client.GetFromJsonAsync<JsonObject>("/v1/admin/search?q=Synchronous");
            Assert.NotEmpty(executionSearch!["executions"]!.AsArray());
            var logsPath = Path.Combine(root, "watch", "Logs");
            Directory.CreateDirectory(logsPath);
            await File.WriteAllTextAsync(Path.Combine(logsPath, "InactivePDF-9999-test.jsonl"), JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, eventName = "conversion", path = "/private/source/searchable-invoice.docx",
                success = false, attempt = 1, durationMs = 123, errorType = "IOException", error = "private engine content"
            }) + "\n");
            var logSearch = await client.GetFromJsonAsync<JsonObject>("/v1/admin/search?q=searchable-invoice");
            Assert.Single(logSearch!["logs"]!["rows"]!.AsArray());
            Assert.DoesNotContain("private engine content", logSearch.ToJsonString());
            Assert.DoesNotContain("/private/source", logSearch.ToJsonString());
            using (var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var streamResponse = await client.GetAsync("/v1/admin/live", HttpCompletionOption.ResponseHeadersRead, streamCancellation.Token))
            {
                Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType!.MediaType);
                using var reader = new StreamReader(await streamResponse.Content.ReadAsStreamAsync(streamCancellation.Token));
                var snapshots = new List<JsonObject>();
                while (snapshots.Count < 2)
                {
                    var line = await reader.ReadLineAsync(streamCancellation.Token);
                    Assert.NotNull(line);
                    if (line.StartsWith("data: ", StringComparison.Ordinal)) snapshots.Add(JsonNode.Parse(line[6..])!.AsObject());
                }
                Assert.NotEqual(snapshots[0]["sampledAt"]!.ToString(), snapshots[1]["sampledAt"]!.ToString());
                Assert.True(snapshots[1]["status"]!["uptimeSeconds"]!.GetValue<double>() > snapshots[0]["status"]!["uptimeSeconds"]!.GetValue<double>());
                streamCancellation.Cancel();
            }
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/settings")).StatusCode);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            Directory.Delete(root, true);
        }
    }
}
