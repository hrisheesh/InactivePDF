using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class ApiKeyManagementApiTests
{
    private static readonly string[] ProfilesReadScope = ["profiles:read"];
    private static readonly string[] InvalidScope = ["not-a-scope"];

    [Fact]
    public async Task AdministratorCanCreateAndUseAReadOnlyIntegrationKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-api-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settingsPath, System.Text.Json.JsonSerializer.Serialize(new InactivePdfSettings()));
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
            using var missing = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync("/v1/watermark-profiles")).StatusCode);

            using var response = await admin.PostAsJsonAsync("/v1/admin/api-keys", new { name = "build-agent" });
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, responseBody);
            var created = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(created);
            Assert.False(string.IsNullOrWhiteSpace(created!["secret"]?.GetValue<string>()));
            Assert.Equal("build-agent", created["key"]!["name"]!.GetValue<string>());

            var secret = created["secret"]!.GetValue<string>();
            using var limitsResponse = await admin.PutAsJsonAsync($"/v1/admin/api-keys/{created["key"]!["id"]!.GetValue<Guid>():D}/limits", new
            {
                requestsPerMinute = 12,
                concurrentConversions = 1,
                maximumQueuedJobs = 8,
                maximumFilesPerRequest = 3,
                maximumRequestBytes = 1,
                maximumFileBytes = 1,
                maximumOutputBytes = 2048,
                dailyInputBytes = 4096,
                jobRetentionDays = 7
            });
            Assert.Equal(HttpStatusCode.OK, limitsResponse.StatusCode);
            var limitsBody = await limitsResponse.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(12, limitsBody!["limits"]!["requestsPerMinute"]!.GetValue<int>());
            Assert.Equal(7, limitsBody["limits"]!["jobRetentionDays"]!.GetValue<int>());
            using var integration = factory.CreateClient();
            integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync("/v1/watermark-profiles")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync("/v1/profiles")).StatusCode);
            using var rejected = await integration.PostAsJsonAsync("/v1/create-text-pdf", new { text = "This request exceeds the configured key limit." });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
            var rejectedBody = await rejected.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("request_bytes_exceeded", rejectedBody!["code"]!.GetValue<string>());
            Assert.Equal("1", rejectedBody["configuredLimit"]!.GetValue<string>());
            Assert.False(rejectedBody["retryable"]!.GetValue<bool>());
            Assert.Equal(rejectedBody["requestId"]!.GetValue<string>(), rejected.Headers.GetValues("X-Request-ID").Single());
            Assert.Equal(HttpStatusCode.Forbidden, (await integration.GetAsync("/v1/admin/status")).StatusCode);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AdministratorCanRotateRevokeAndDeleteKeysWithoutSecretRedactionRegressions()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-api-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settingsPath, System.Text.Json.JsonSerializer.Serialize(new InactivePdfSettings()));
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
            using var createResponse = await admin.PostAsJsonAsync("/v1/admin/api-keys", new
            {
                name = "rotating-agent",
                scopes = ProfilesReadScope
            });
            var created = await createResponse.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var id = created!["key"]!["id"]!.GetValue<Guid>();
            var firstSecret = created["secret"]!.GetValue<string>();

            var list = await admin.GetStringAsync("/v1/admin/api-keys");
            Assert.Contains("rotating-agent", list, StringComparison.Ordinal);
            Assert.DoesNotContain(firstSecret, list, StringComparison.Ordinal);
            var get = await admin.GetStringAsync($"/v1/admin/api-keys/{id:D}");
            Assert.DoesNotContain(firstSecret, get, StringComparison.Ordinal);

            using var rotateResponse = await admin.PostAsJsonAsync($"/v1/admin/api-keys/{id:D}/rotate", new { });
            var rotated = await rotateResponse.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
            var secondSecret = rotated!["secret"]!.GetValue<string>();
            Assert.NotEqual(firstSecret, secondSecret);

            using var firstClient = factory.CreateClient();
            firstClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstSecret);
            Assert.Equal(HttpStatusCode.Unauthorized, (await firstClient.GetAsync("/v1/watermark-profiles")).StatusCode);
            using var secondClient = factory.CreateClient();
            secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondSecret);
            Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/v1/watermark-profiles")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await secondClient.PostAsJsonAsync("/v1/create-text-pdf", new { text = "not permitted" })).StatusCode);

            using var revokeResponse = await admin.PostAsync($"/v1/admin/api-keys/{id:D}/revoke", content: null);
            Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
            Assert.DoesNotContain(secondSecret, await revokeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, (await secondClient.GetAsync("/v1/watermark-profiles")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/admin/api-keys/{id:D}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1/admin/api-keys/{id:D}")).StatusCode);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InvalidScopesAndExpiredKeysAreRejectedAtTheManagementBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-api-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settingsPath, System.Text.Json.JsonSerializer.Serialize(new InactivePdfSettings()));
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
            using var invalidScope = await admin.PostAsJsonAsync("/v1/admin/api-keys", new { name = "bad", scopes = InvalidScope });
            Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);
            using var expired = await admin.PostAsJsonAsync("/v1/admin/api-keys", new { name = "expired", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
