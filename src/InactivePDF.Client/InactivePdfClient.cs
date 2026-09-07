using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace InactivePDF.Client;

public sealed class InactivePdfClientOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public int MaximumAttempts { get; set; } = 3;
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan DefaultPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

public sealed class InactivePdfFile
{
    public InactivePdfFile(Stream content, string fileName, string? contentType = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        FileName = string.IsNullOrWhiteSpace(fileName) ? throw new ArgumentException("A filename is required.", nameof(fileName)) : fileName;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType!;
    }

    public Stream Content { get; }
    public string FileName { get; }
    public string ContentType { get; }
}

public enum InactivePdfJobState
{
    Accepted,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Retrying,
    DeadLettered
}

public enum InactivePdfOperation
{
    ConvertFile,
    ConvertFiles,
    ConvertAndMerge,
    CreateTextPdf
}

public sealed class InactivePdfJobRequest
{
    public InactivePdfOperation Operation { get; set; } = InactivePdfOperation.ConvertFile;
    public IList<InactivePdfFile> Files { get; } = new List<InactivePdfFile>();
    public string? Text { get; set; }
    public string? CorrelationId { get; set; }
    public string Profile { get; set; } = "archive";
    public string? WatermarkProfile { get; set; }
    /// <summary>Optional serialized WatermarkOptions JSON for a direct watermark.</summary>
    public string? WatermarkJson { get; set; }
}

public sealed class InactivePdfCapabilities
{
    public string Service { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public IReadOnlyList<string> Operations { get; set; } = Array.Empty<string>();
    public IReadOnlyList<InactivePdfFormatDescriptor> Formats { get; set; } = Array.Empty<InactivePdfFormatDescriptor>();
    public IReadOnlyList<InactivePdfProfileDescriptor> Profiles { get; set; } = Array.Empty<InactivePdfProfileDescriptor>();
}

public sealed class InactivePdfFormatDescriptor
{
    public string Extension { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public IReadOnlyList<string> ContentTypes { get; set; } = Array.Empty<string>();
    public bool SupportsVisualLayout { get; set; }
    public bool SupportsLosslessPassThrough { get; set; }
}

public sealed class InactivePdfProfileDescriptor
{
    public string Name { get; set; } = string.Empty;
    public int PdfVersion { get; set; }
    public bool PreserveJpegData { get; set; }
    public bool DownsampleImages { get; set; }
    public int MaximumImageDpi { get; set; }
    public int JpegQuality { get; set; }
    public bool CompressContentStreams { get; set; }
    public bool BestCompression { get; set; }
    public bool PreserveSourceMetadata { get; set; }
    public string FontPolicy { get; set; } = string.Empty;
    public string EncryptionPolicy { get; set; } = string.Empty;
    public string LargeMarkupPolicy { get; set; } = string.Empty;
    public bool StructuralValidation { get; set; }
}

public sealed class InactivePdfJobStatus
{
    public Guid JobId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public InactivePdfOperation Operation { get; set; }
    public InactivePdfJobState State { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Attempts { get; set; }
    public string? OutputPath { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsComplete => State is InactivePdfJobState.Succeeded or InactivePdfJobState.Failed or InactivePdfJobState.Cancelled or InactivePdfJobState.DeadLettered;
    public bool IsSuccessful => State == InactivePdfJobState.Succeeded;
}

public sealed class InactivePdfQueueStatus
{
    public int Count { get; set; }
    public int Capacity { get; set; }
}

public sealed class InactivePdfAttemptDiagnostic
{
    public int Attempt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Exception { get; set; } = string.Empty;
    public bool Retryable { get; set; }
}

public sealed class InactivePdfDeadLetter
{
    public Guid JobId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public InactivePdfOperation Operation { get; set; }
    public int Attempt { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorType { get; set; } = string.Empty;
    public string Exception { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public IReadOnlyList<InactivePdfAttemptDiagnostic> AttemptHistory { get; set; } = Array.Empty<InactivePdfAttemptDiagnostic>();
}

public sealed class InactivePdfClientException : Exception
{
    public InactivePdfClientException(HttpStatusCode statusCode, string responseBody)
        : base($"InactivePDF returned {(int)statusCode} ({statusCode}). {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }
}

/// <summary>
/// Language-neutral HTTP client for synchronous and asynchronous InactivePDF operations.
/// The client never disposes caller-owned input streams.
/// </summary>
public sealed class InactivePdfClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly InactivePdfClientOptions _options;

    public InactivePdfClient(Uri baseAddress, InactivePdfClientOptions? options = null)
        : this(new HttpClient { BaseAddress = EnsureBaseAddress(baseAddress) }, true, options)
    {
    }

    public InactivePdfClient(HttpClient httpClient, InactivePdfClientOptions? options = null)
        : this(httpClient, false, options)
    {
    }

    private InactivePdfClient(HttpClient httpClient, bool ownsHttpClient, InactivePdfClientOptions? options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _options = options ?? new InactivePdfClientOptions();
        if (_options.MaximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaximumAttempts must be positive.");
        if (_options.RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be positive.");
    }

    public async Task<InactivePdfCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/capabilities"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<InactivePdfCapabilities>(response).ConfigureAwait(false);
    }

    public Task<byte[]> ConvertFileAsync(Stream content, string fileName, string? contentType = null, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateMultipartRequest("v1/convert-file", new[] { new InactivePdfFile(content, fileName, contentType) }), cancellationToken);

    public Task ConvertFileAsync(Stream content, string fileName, Stream destination, string? contentType = null, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => CreateMultipartRequest("v1/convert-file", new[] { new InactivePdfFile(content, fileName, contentType) }), destination, cancellationToken);

    public Task<byte[]> ConvertFileWithProfileAsync(Stream content, string fileName, string profile, string? contentType = null, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateMultipartRequest("v1/convert-file", new[] { new InactivePdfFile(content, fileName, contentType) }, profile), cancellationToken);

    public Task<byte[]> ConvertFileWithWatermarkAsync(Stream content, string fileName, string watermarkJson, string? contentType = null, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateMultipartRequest("v1/convert-file", new[] { new InactivePdfFile(content, fileName, contentType) }, watermarkJson: watermarkJson), cancellationToken);

    public Task ConvertFileWithProfileAsync(Stream content, string fileName, string profile, Stream destination, string? contentType = null, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => CreateMultipartRequest("v1/convert-file", new[] { new InactivePdfFile(content, fileName, contentType) }, profile), destination, cancellationToken);

    public Task<byte[]> ConvertAndMergeAsync(IReadOnlyList<InactivePdfFile> files, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateMultipartRequest("v1/convert-and-merge", files), cancellationToken);

    public Task ConvertAndMergeAsync(IReadOnlyList<InactivePdfFile> files, Stream destination, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => CreateMultipartRequest("v1/convert-and-merge", files), destination, cancellationToken);

    public Task<byte[]> ConvertAndMergeWithProfileAsync(IReadOnlyList<InactivePdfFile> files, string profile, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateMultipartRequest("v1/convert-and-merge", files, profile), cancellationToken);

    public Task<byte[]> ConvertAndMergeWithWatermarkAsync(IReadOnlyList<InactivePdfFile> files, string watermarkJson, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateMultipartRequest("v1/convert-and-merge", files, watermarkJson: watermarkJson), cancellationToken);

    public Task ConvertAndMergeWithProfileAsync(IReadOnlyList<InactivePdfFile> files, string profile, Stream destination, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => CreateMultipartRequest("v1/convert-and-merge", files, profile), destination, cancellationToken);

    public Task<byte[]> CreateTextPdfAsync(string text, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateJsonRequest("v1/create-text-pdf", new { text = text ?? string.Empty }), cancellationToken);

    public Task CreateTextPdfAsync(string text, Stream destination, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => CreateJsonRequest("v1/create-text-pdf", new { text = text ?? string.Empty }), destination, cancellationToken);

    public Task<byte[]> CreateTextPdfWithProfileAsync(string text, string profile, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateJsonRequest("v1/create-text-pdf", new { text = text ?? string.Empty, profile }), cancellationToken);

    public Task<byte[]> CreateTextPdfWithWatermarkAsync(string text, string watermarkJson, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => CreateJsonRequest("v1/create-text-pdf", new { text = text ?? string.Empty, watermark = JsonSerializer.Deserialize<JsonElement>(watermarkJson) }), cancellationToken);

    public Task CreateTextPdfWithProfileAsync(string text, string profile, Stream destination, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => CreateJsonRequest("v1/create-text-pdf", new { text = text ?? string.Empty, profile }), destination, cancellationToken);

    public async Task<InactivePdfJobStatus> SubmitJobAsync(InactivePdfJobRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        using var response = await SendWithRetryAsync(() => CreateJobRequest(request), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<InactivePdfJobStatus>(response).ConfigureAwait(false);
    }

    public async Task<InactivePdfJobStatus?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{jobId:D}"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<InactivePdfJobStatus>(response).ConfigureAwait(false);
    }

    public async Task<InactivePdfJobStatus> WaitForCompletionAsync(
        Guid jobId,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout ?? _options.RequestTimeout);
        var interval = pollInterval ?? _options.DefaultPollInterval;
        while (true)
        {
            var status = await GetJobStatusAsync(jobId, timeoutCancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"InactivePDF job {jobId:D} was not found.");
            if (status.IsComplete) return status;
            await Task.Delay(interval, timeoutCancellation.Token).ConfigureAwait(false);
        }
    }

    public Task<byte[]> DownloadJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{jobId:D}/output"), cancellationToken);

    public Task DownloadJobOutputAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default) =>
        DownloadToStreamAsync(() => new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{jobId:D}/output"), destination, cancellationToken);

    public async Task<InactivePdfQueueStatus> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/queue"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<InactivePdfQueueStatus>(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InactivePdfDeadLetter>> GetDeadLettersAsync(
        int maximumItems = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"v1/dead-letters?limit={maximumItems}"),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<IReadOnlyList<InactivePdfDeadLetter>>(response).ConfigureAwait(false);
    }

    public async Task<InactivePdfJobStatus> ReplayDeadLetterAsync(
        Guid jobId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job ID is required.", nameof(jobId));
        using var response = await SendWithRetryAsync(
            () => CreateJsonRequest($"v1/dead-letters/{jobId:D}/replay", new { correlationId }),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<InactivePdfJobStatus>(response).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<byte[]> DownloadBytesAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await DownloadToStreamAsync(requestFactory, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task DownloadToStreamAsync(Func<HttpRequestMessage> requestFactory, Stream destination, CancellationToken cancellationToken)
    {
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        using var response = await SendWithRetryAsync(requestFactory, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await input.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            using var request = requestFactory();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!ShouldRetry(response.StatusCode) || attempt == _options.MaximumAttempts) return response;
                response.Dispose();
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempt < _options.MaximumAttempts)
            {
                lastException = exception;
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }

        throw new HttpRequestException("InactivePDF request failed after all retry attempts.", lastException);
    }

    private static HttpRequestMessage CreateMultipartRequest(string path, IEnumerable<InactivePdfFile> files, string? profile = null, string? watermarkProfile = null, string? watermarkJson = null)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var file in files ?? throw new ArgumentNullException(nameof(files)))
        {
            var content = new StreamContent(new NonDisposingStream(file.Content));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
            multipart.Add(content, "file", file.FileName);
        }
        if (!string.IsNullOrWhiteSpace(profile)) multipart.Add(new StringContent(profile), "profile");
        if (!string.IsNullOrWhiteSpace(watermarkProfile)) multipart.Add(new StringContent(watermarkProfile), "watermarkProfile");
        if (!string.IsNullOrWhiteSpace(watermarkJson)) multipart.Add(new StringContent(watermarkJson, Encoding.UTF8), "watermark");

        return new HttpRequestMessage(HttpMethod.Post, path) { Content = multipart };
    }

    private static HttpRequestMessage CreateJsonRequest(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpRequestMessage CreateJobRequest(InactivePdfJobRequest request)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(request.Operation.ToString()), "operation");
        multipart.Add(new StringContent(request.Profile ?? "archive"), "profile");
        if (!string.IsNullOrWhiteSpace(request.WatermarkProfile)) multipart.Add(new StringContent(request.WatermarkProfile), "watermarkProfile");
        if (!string.IsNullOrWhiteSpace(request.WatermarkJson)) multipart.Add(new StringContent(request.WatermarkJson, Encoding.UTF8), "watermark");
        if (!string.IsNullOrWhiteSpace(request.CorrelationId)) multipart.Add(new StringContent(request.CorrelationId), "correlationId");
        if (request.Text is not null) multipart.Add(new StringContent(request.Text, Encoding.UTF8), "text");
        foreach (var file in request.Files)
        {
            var content = new StreamContent(new NonDisposingStream(file.Content));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
            multipart.Add(content, "file", file.FileName);
        }

        var result = new HttpRequestMessage(HttpMethod.Post, "v1/jobs") { Content = multipart };
        if (!string.IsNullOrWhiteSpace(request.CorrelationId)) result.Headers.TryAddWithoutValidation("Idempotency-Key", request.CorrelationId);
        return result;
    }

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false);
        return value ?? throw new InvalidDataException("InactivePDF returned an empty JSON response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 200 and <= 299) return;
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InactivePdfClientException(response.StatusCode, body);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout || statusCode == (HttpStatusCode)429 || (int)statusCode >= 500;

    private static bool IsTransient(Exception exception, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException;

    private TimeSpan GetRetryDelay(int attempt)
    {
        var factor = Math.Min(6, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(30_000, _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, factor)));
    }

    private static Uri EnsureBaseAddress(Uri value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? value : new Uri(value.AbsoluteUri + "/");
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
