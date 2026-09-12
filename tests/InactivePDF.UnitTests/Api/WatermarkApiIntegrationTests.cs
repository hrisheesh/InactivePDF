using System.Net;
using System.Net.Http.Json;
using InactivePDF.Domain.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace InactivePDF.UnitTests.Api;

[Collection("API integration")]
public sealed class WatermarkApiIntegrationTests
{
    [Fact]
    public async Task AdministratorProfileRoutesManageWatermarkProfilesSeparately()
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
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
            var body = new { name = "admin-review", type = "watermark", options = new WatermarkOptions(Text: "REVIEW") };
            using var create = await client.PostAsync("/v1/admin/profiles", JsonContent.Create(body, options: jsonOptions));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            Assert.Equal("admin-review", (await client.GetFromJsonAsync<JsonObject>("/v1/profiles/admin-review", jsonOptions))!["name"]!.GetValue<string>());

            using var update = await client.PutAsync("/v1/admin/profiles/admin-review", JsonContent.Create(new { type = "watermark", options = new WatermarkOptions(Text: "UPDATED") }, options: jsonOptions));
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var updated = await client.GetFromJsonAsync<JsonObject>("/v1/profiles/admin-review", jsonOptions);
            Assert.Equal("UPDATED", updated!["watermark"]!["options"]!["text"]!.GetValue<string>());

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/v1/admin/profiles/admin-review?type=watermark")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/profiles/admin-review")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task UnifiedProfileApiSeparatesPdfOutputAndWatermarkProfiles()
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
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
            var profile = new WatermarkOptions(Text: "REVIEW", Opacity: .4, Pages: "1,3-5");
            using var save = await client.PutAsync("/v1/watermark-profiles/review", JsonContent.Create(profile, options: jsonOptions));
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);

            var catalog = await client.GetFromJsonAsync<JsonObject>("/v1/profiles", jsonOptions);
            Assert.NotNull(catalog);
            Assert.Equal(3, catalog!["pdfOutput"]!.AsArray().Count);
            Assert.Contains(catalog["pdfOutput"]!.AsArray(), item => string.Equals(item!["name"]!.GetValue<string>(), "archive", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(catalog["watermark"]!.AsArray(), item => string.Equals(item!["name"]!.GetValue<string>(), "review", StringComparison.OrdinalIgnoreCase));

            var outputLookup = await client.GetFromJsonAsync<JsonObject>("/v1/profiles/archive", jsonOptions);
            Assert.Equal("archive", outputLookup!["name"]!.GetValue<string>());
            Assert.Equal("archive", outputLookup["pdfOutput"]!["name"]!.GetValue<string>());
            Assert.Null(outputLookup["watermark"]);

            var watermarkLookup = await client.GetFromJsonAsync<JsonObject>("/v1/profiles/review", jsonOptions);
            Assert.Equal("review", watermarkLookup!["name"]!.GetValue<string>());
            Assert.Equal("REVIEW", watermarkLookup["watermark"]!["options"]!["text"]!.GetValue<string>());
            Assert.Null(watermarkLookup["pdfOutput"]);

            using var conversionContent = new MultipartFormDataContent();
            conversionContent.Add(new StringContent("async"), "mode");
            conversionContent.Add(new StringContent("compact"), "profile");
            conversionContent.Add(new StringContent("review"), "watermarkProfile");
            conversionContent.Add(new ByteArrayContent("profile snapshot"u8.ToArray()), "file", "snapshot.txt");
            using var accepted = await client.PostAsync("/v1/conversions", conversionContent);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("compact", acceptedBody!["profile"]!.GetValue<string>());
            Assert.Equal("review", acceptedBody["watermarkProfile"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/profiles/missing")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

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

    [Fact]
    public async Task UnifiedConversionEndpointSupportsSyncAsyncAndIdempotencyHeaders()
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

            using var syncContent = new MultipartFormDataContent();
            syncContent.Add(new StringContent("sync"), "mode");
            syncContent.Add(new StringContent("archive"), "profile");
            syncContent.Add(new ByteArrayContent("unified sync input"u8.ToArray()), "file", "sync.txt");
            using var syncRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/conversions") { Content = syncContent };
            syncRequest.Headers.Add("X-Request-Id", "unified-sync-1");
            using var syncResponse = await client.SendAsync(syncRequest);
            Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);
            Assert.Equal("unified-sync-1", syncResponse.Headers.GetValues("X-Request-ID").Single());
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(await syncResponse.Content.ReadAsByteArrayAsync(), 0, 5));

            using var asyncContent = new MultipartFormDataContent();
            asyncContent.Add(new StringContent("async"), "mode");
            asyncContent.Add(new StringContent("archive"), "profile");
            asyncContent.Add(new StringContent("unified-async-1"), "correlationId");
            asyncContent.Add(new ByteArrayContent("unified async input"u8.ToArray()), "file", "async.txt");
            using var asyncRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/conversions") { Content = asyncContent };
            asyncRequest.Headers.Add("Idempotency-Key", "unified-async-1");
            using var accepted = await client.SendAsync(asyncRequest);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.NotNull(accepted.Headers.Location);
            var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(acceptedBody!["jobId"]);

            using var duplicateContent = new MultipartFormDataContent();
            duplicateContent.Add(new StringContent("async"), "mode");
            duplicateContent.Add(new StringContent("archive"), "profile");
            duplicateContent.Add(new StringContent("unified-async-1"), "correlationId");
            duplicateContent.Add(new ByteArrayContent("unified async input"u8.ToArray()), "file", "async.txt");
            using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/conversions") { Content = duplicateContent };
            duplicateRequest.Headers.Add("Idempotency-Key", "unified-async-1");
            using var duplicate = await client.SendAsync(duplicateRequest);
            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            Assert.Equal(accepted.Headers.Location, duplicate.Headers.Location);

            using var conflictContent = new MultipartFormDataContent();
            conflictContent.Add(new StringContent("async"), "mode");
            conflictContent.Add(new StringContent("archive"), "profile");
            conflictContent.Add(new StringContent("unified-async-1"), "correlationId");
            conflictContent.Add(new ByteArrayContent("different input"u8.ToArray()), "file", "async.txt");
            using var conflictRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/conversions") { Content = conflictContent };
            conflictRequest.Headers.Add("Idempotency-Key", "unified-async-1");
            using var conflict = await client.SendAsync(conflictRequest);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task WatermarkAssetCanBeUploadedListedAndServedFromProtectedRoot()
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
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new ByteArrayContent("test-image"u8.ToArray()), "file", "brand.png");
            var upload = await client.PostAsync("/v1/watermark-assets", multipart);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var names = await client.GetFromJsonAsync<string[]>("/v1/watermark-assets");
            Assert.Contains("brand.png", names!);
            var asset = await client.GetAsync("/v1/watermark-assets/brand.png");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("test-image", await asset.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/watermark-assets/..%2Foutside.png")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_DATA_PATH", previous);
            Environment.SetEnvironmentVariable("INACTIVEPDF_API_TOKEN", oldToken);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
