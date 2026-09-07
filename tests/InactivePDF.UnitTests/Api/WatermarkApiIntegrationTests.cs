using System.Net;
using System.Net.Http.Json;
using InactivePDF.Domain.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class WatermarkApiIntegrationTests
{
    [Fact]
    public async Task ProfileCrudIsAvailableThroughPublicApi()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("INACTIVEPDF_DATA_PATH");
        var oldToken = Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN");
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", null);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            var profile = new WatermarkOptions(Text: "DRAFT", Opacity: .35, Pages: "1,3-5");
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
            var put = await client.PutAsync("/v1/watermark-profiles/review", JsonContent.Create(profile, options: jsonOptions));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var get = await client.GetFromJsonAsync<WatermarkOptions>("/v1/watermark-profiles/review", jsonOptions);
            Assert.Equal(profile, get);
            var list = await client.GetFromJsonAsync<Dictionary<string, WatermarkOptions>>("/v1/watermark-profiles", jsonOptions);
            Assert.NotNull(list);
            Assert.Contains("review", list.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/v1/watermark-profiles/review")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ConfiguredTokenProtectsPublicApiButNotHealth()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("INACTIVEPDF_DATA_PATH");
        var oldToken = Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN");
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", "integration-secret");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/watermark-profiles")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-secret");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/watermark-profiles")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-secret");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/watermark-profiles")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TextConversionAcceptsDirectWatermarkOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("INACTIVEPDF_DATA_PATH");
        var oldToken = Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN");
        Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", root);
        Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", null);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync("/v1/create-text-pdf", new
            {
                text = "integration test",
                profile = "archive",
                watermark = new { kind = "Text", text = "TEST", opacity = .25, pages = "all" }
            });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 500);
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
