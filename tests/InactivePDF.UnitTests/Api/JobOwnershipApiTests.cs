using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class JobOwnershipApiTests
{
    [Fact]
    public async Task AnIntegrationKeyCannotReadAnotherKeysJobOrOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-ownership-" + Guid.NewGuid().ToString("N"));
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
            var owner = await CreateKeyAsync(admin, "owner");
            var other = await CreateKeyAsync(admin, "other");

            using var ownerClient = factory.CreateClient();
            ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Secret);
            using var form = new MultipartFormDataContent
            {
                { new StringContent("CreateTextPdf"), "operation" },
                { new StringContent("private job"), "text" },
                { new StringContent("ownership-check"), "correlationId" }
            };
            using var accepted = await ownerClient.PostAsync("/v1/jobs", form);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var job = await accepted.Content.ReadFromJsonAsync<JsonObject>();
            var jobId = job!["jobId"]!.GetValue<Guid>();

            using var otherClient = factory.CreateClient();
            otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Secret);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/v1/jobs/{jobId:D}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/v1/jobs/{jobId:D}/output")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/v1/jobs/{jobId:D}")).StatusCode);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<(Guid Id, string Secret)> CreateKeyAsync(HttpClient admin, string name)
    {
        using var response = await admin.PostAsJsonAsync("/v1/admin/api-keys", new { name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return (body!["key"]!["id"]!.GetValue<Guid>(), body["secret"]!.GetValue<string>());
    }
}
