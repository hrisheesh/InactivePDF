using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class JobLifecycleApiTests
{
    private static readonly string[] LifecycleScopes = ["convert:submit", "jobs:read", "jobs:cancel"];
    private static readonly string[] AcceptableCancellationStates = ["Cancelled", "Succeeded", "DeadLettered"];

    [Fact]
    public async Task PublicJobListingIsOwnedAndExcludesServerPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new InactivePdfSettings()));
        var names = new[] { "INACTIVEPDF_SETTINGS_PATH", "INACTIVEPDF_DATA_PATH", "INACTIVEPDF_STATE_PATH", "INACTIVEPDF_JOBS_PATH", "INACTIVEPDF_WATCH_ROOT", "INACTIVEPDF_API_TOKEN" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("INACTIVEPDF_SETTINGS_PATH", settingsPath);
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_STATE_PATH", Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_JOBS_PATH", Path.Combine(root, "jobs"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_ROOT", Path.Combine(root, "watch"));
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", "administrator-secret");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var admin = factory.CreateClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "administrator-secret");
            var keyResponse = await admin.PostAsJsonAsync("/v1/admin/api-keys", new
            {
                name = "lifecycle",
                scopes = LifecycleScopes
            });
            keyResponse.EnsureSuccessStatusCode();
            var key = await keyResponse.Content.ReadFromJsonAsync<JsonObject>();

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key!["secret"]!.GetValue<string>());
            using var form = new MultipartFormDataContent
            {
                { new StringContent("CreateTextPdf"), "operation" },
                { new StringContent("lifecycle test"), "text" },
                { new StringContent("lifecycle-check"), "correlationId" }
            };
            using var accepted = await client.PostAsync("/v1/jobs", form);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonObject>();
            var jobId = acceptedBody!["jobId"]!.GetValue<Guid>();

            var page = await client.GetFromJsonAsync<JsonObject>("/v1/jobs?source=API&format=txt&limit=10");
            var item = Assert.Single(page!["items"]!.AsArray());
            Assert.Equal(jobId, item!["jobId"]!.GetValue<Guid>());
            Assert.Null(item["outputPath"]);
            Assert.NotNull(item["progressPercent"]);
            Assert.NotNull(item["correlationId"]);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/jobs?cursor=not-a-cursor")).StatusCode);

            using var cancelled = await client.PostAsync($"/v1/jobs/{jobId:D}/cancel", content: null);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var cancellationBody = await cancelled.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Contains(cancellationBody!["state"]!.GetValue<string>(), AcceptableCancellationStates);

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/v1/jobs/{jobId:D}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/jobs/{jobId:D}")).StatusCode);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
