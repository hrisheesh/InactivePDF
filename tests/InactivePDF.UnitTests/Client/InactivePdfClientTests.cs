using System.Net;
using System.Net.Http;
using System.Text;
using InactivePDF.Client;

namespace InactivePDF.UnitTests.Client;

public sealed class InactivePdfClientTests
{
    [Fact]
    public async Task ProfileCatalogIsTyped()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("v1/profiles", request.RequestUri!.AbsolutePath.TrimStart('/'));
            return Task.FromResult(JsonResponse("{\"pdfOutput\":[{\"name\":\"archive\",\"pdfVersion\":17}],\"watermark\":[{\"name\":\"review\",\"options\":{\"text\":\"REVIEW\"}}]}"));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient);

        var profiles = await client.GetProfilesAsync();

        Assert.Single(profiles.PdfOutput);
        Assert.Equal("archive", profiles.PdfOutput[0].Name);
        Assert.Single(profiles.Watermark);
        Assert.Equal("review", profiles.Watermark[0].Name);
        Assert.Equal("REVIEW", profiles.Watermark[0].Options.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ConvertFileStreamsPdfAndLeavesInputOpen()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("v1/convert-file", request.RequestUri!.AbsolutePath.TrimStart('/'));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pdf"))
            });
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("input"));
        using var output = new MemoryStream();

        await client.ConvertFileAsync(input, "input.txt", output);

        Assert.Equal("pdf", Encoding.UTF8.GetString(output.ToArray()));
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task TransientResponseIsRetried()
    {
        var attempts = 0;
        var handler = new StubHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"operations\":[\"convert-file\"]}", Encoding.UTF8, "application/json") });
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient, new InactivePdfClientOptions { InitialRetryDelay = TimeSpan.Zero });

        var capabilities = await client.GetCapabilitiesAsync();

        Assert.Equal(2, attempts);
        Assert.Single(capabilities.Operations);
        Assert.Equal("convert-file", capabilities.Operations[0]);
    }

    [Fact]
    public async Task JobSubmissionAndStatusAreTyped()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("jobs", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("{\"jobId\":\"11111111-1111-1111-1111-111111111111\",\"correlationId\":\"x\",\"operation\":0,\"state\":0,\"acceptedAt\":\"2026-08-09T00:00:00+00:00\",\"updatedAt\":\"2026-08-09T00:00:00+00:00\",\"attempts\":0}"));
            }

            return Task.FromResult(JsonResponse("{\"jobId\":\"11111111-1111-1111-1111-111111111111\",\"correlationId\":\"x\",\"operation\":0,\"state\":2,\"acceptedAt\":\"2026-08-09T00:00:00+00:00\",\"updatedAt\":\"2026-08-09T00:00:01+00:00\",\"attempts\":1}"));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient);

        var submitted = await client.SubmitJobAsync(new InactivePdfJobRequest { CorrelationId = "x" });
        var status = await client.GetJobStatusAsync(submitted.JobId);

        Assert.Equal(InactivePdfJobState.Accepted, submitted.State);
        Assert.NotNull(status);
        Assert.True(status!.IsSuccessful);
    }

    [Fact]
    public async Task UnifiedConversionSupportsAsyncModeAndRequestHeaders()
    {
        var jobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal("v1/conversions", request.RequestUri!.AbsolutePath.TrimStart('/'));
            Assert.Equal("conversion-1", request.Headers.GetValues("Idempotency-Key").Single());
            Assert.Equal("trace-1", request.Headers.GetValues("X-Request-Id").Single());
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("mode", body, StringComparison.Ordinal);
            Assert.Contains("async", body, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Headers = { Location = new Uri($"/v1/jobs/{jobId:D}", UriKind.Relative) },
                Content = new StringContent($"{{\"jobId\":\"{jobId:D}\",\"correlationId\":\"conversion-1\",\"operation\":0,\"state\":0,\"acceptedAt\":\"2026-09-12T00:00:00Z\",\"updatedAt\":\"2026-09-12T00:00:00Z\",\"attempts\":0}}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("input"));

        var result = await client.ConvertAsync(new InactivePdfConversionRequest(input, "input.txt")
        {
            IdempotencyKey = "conversion-1",
            RequestId = "trace-1"
        });

        Assert.False(result.IsSynchronous);
        Assert.Equal(jobId, result.Job!.JobId);
        Assert.Equal(new Uri("http://localhost/v1/jobs/" + jobId.ToString("D")), result.Location);
    }

    [Fact]
    public async Task UnifiedConversionSupportsSyncModeAndReturnsPdfBytes()
    {
        var handler = new StubHandler(async (request, _) =>
        {
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("mode", body, StringComparison.Ordinal);
            Assert.Contains("sync", body, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pdf")) };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("input"));

        var result = await client.ConvertAsync(new InactivePdfConversionRequest(input, "input.txt")
        {
            Mode = InactivePdfConversionMode.Sync
        });

        Assert.True(result.IsSynchronous);
        Assert.Equal("pdf", Encoding.UTF8.GetString(result.Pdf!));
    }

    [Fact]
    public async Task JobLifecycleClientMethodsUsePublicLifecycleRoutes()
    {
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var calls = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"items\":[{\"jobId\":\"33333333-3333-3333-3333-333333333333\",\"state\":7,\"progressPercent\":25,\"currentLane\":\"Office\"}],\"nextCursor\":\"next\"}"));
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse("{\"jobId\":\"33333333-3333-3333-3333-333333333333\",\"state\":7,\"progressPercent\":25}"));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new InactivePdfClient(httpClient);

        var page = await client.GetJobsAsync(new InactivePdfJobQuery { Source = "API", Format = "docx", Limit = 10 });
        var cancelled = await client.CancelJobAsync(jobId);
        var retried = await client.RetryJobAsync(jobId);
        await client.DeleteJobAsync(jobId);

        Assert.Equal("next", page.NextCursor);
        Assert.Equal(InactivePdfJobState.Queued, page.Items[0].State);
        Assert.Equal(25, page.Items[0].ProgressPercent);
        Assert.Equal("Office", page.Items[0].CurrentLane);
        Assert.Equal(jobId, cancelled.JobId);
        Assert.Equal(jobId, retried.JobId);
        Assert.Contains("GET /v1/jobs", calls);
        Assert.Contains("POST /v1/jobs/33333333-3333-3333-3333-333333333333/cancel", calls);
        Assert.Contains("POST /v1/jobs/33333333-3333-3333-3333-333333333333/retry", calls);
        Assert.Contains("DELETE /v1/jobs/33333333-3333-3333-3333-333333333333", calls);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
