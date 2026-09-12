using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class UsageAndAuditApiTests
{
    [Fact]
    public async Task UsageIsAvailableByKeyAndAdminOnlyAndAuditEventsContainNoSecret()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-usage-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousData = Environment.GetEnvironmentVariable("INACTIVEPDF_DATA_PATH");
        var previousToken = Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN");
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", null);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var admin = factory.CreateClient();
            var created = await admin.PostAsJsonAsync("/v1/admin/api-keys", new { name = "usage-test" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdJson = await created.Content.ReadFromJsonAsync<JsonObject>();
            var keyId = createdJson!["key"]!["id"]!.GetValue<Guid>();
            var secret = createdJson["secret"]!.GetValue<string>();

            using var integration = factory.CreateClient();
            integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var conversion = await integration.PostAsJsonAsync("/v1/create-text-pdf", new { text = "usage tracking" });
            Assert.True(conversion.IsSuccessStatusCode, await conversion.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await integration.GetAsync("/v1/admin/usage")).StatusCode);

            var usage = await admin.GetFromJsonAsync<JsonObject>("/v1/admin/usage");
            Assert.NotNull(usage);
            var item = usage!["items"]!.AsArray().Single(row => row!["apiKeyId"]!.GetValue<Guid>() == keyId)!;
            Assert.True(item["totalRequests"]!.GetValue<long>() >= 1);
            Assert.True(item["successfulConversions"]!.GetValue<long>() >= 1);
            Assert.True(item["bytesReceived"]!.GetValue<long>() > 0);
            Assert.True(item["bytesProduced"]!.GetValue<long>() > 0);

            var byKey = await admin.GetFromJsonAsync<JsonObject>($"/v1/admin/api-keys/{keyId:D}/usage");
            Assert.Equal(keyId, byKey!["apiKeyId"]!.GetValue<Guid>());
            Assert.True(byKey["successfulConversions"]!.GetValue<long>() >= 1);

            var audit = await admin.GetFromJsonAsync<JsonObject>("/v1/admin/audit-events?limit=50");
            Assert.NotNull(audit);
            Assert.NotEmpty(audit!["events"]!.AsArray());
            var auditText = audit.ToJsonString();
            Assert.DoesNotContain(secret, auditText, StringComparison.Ordinal);
            Assert.DoesNotContain("usage tracking", auditText, StringComparison.Ordinal);
            Assert.Contains(audit["events"]!.AsArray(), row => row!["method"]!.GetValue<string>() == "POST");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previousData);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", previousToken);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
