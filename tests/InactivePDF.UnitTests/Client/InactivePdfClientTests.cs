using System.Net;
using System.Net.Http;
using System.Text;
using InactivePDF.Client;

namespace InactivePDF.UnitTests.Client;

public sealed class InactivePdfClientTests
{
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

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
