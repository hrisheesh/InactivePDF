using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = ReleaseOptions.Parse(args);
using var http = new HttpClient { BaseAddress = options.BaseUri, Timeout = options.Timeout };
if (!string.IsNullOrWhiteSpace(options.Token))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

var checks = new List<string>();
var failures = new List<string>();
var success = true;
var openApiRequiredPaths = new[] { "/v1/conversions", "/v1/jobs", "/v1/watermark-profiles", "/v1/admin/api-keys" };
var capabilityExtensions = new[] { ".docx", ".doc", ".xlsx", ".xls", ".ods", ".pptx", ".odp", ".rtf", ".html", ".txt", ".png", ".pdf" };

async Task CheckAsync(string name, Func<Task> action)
{
    try
    {
        await action().ConfigureAwait(false);
        checks.Add(name);
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        success = false;
        failures.Add($"{name}: {exception.Message}");
        Console.WriteLine($"FAIL  {name}: {exception.Message}");
    }
}

await CheckAsync("health and readiness", async () =>
{
    await ExpectStatusAsync("/health", HttpStatusCode.OK);
    await ExpectStatusAsync("/ready", HttpStatusCode.OK);
});

await CheckAsync("OpenAPI contract", async () =>
{
    using var response = await http.GetAsync("/openapi.json").ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    var document = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))?.AsObject()
        ?? throw new InvalidOperationException("OpenAPI response was not a JSON object.");
    if (document["openapi"]?.GetValue<string>() != "3.0.3") throw new InvalidOperationException("Unexpected OpenAPI version.");
    foreach (var path in openApiRequiredPaths)
        if (document["paths"]?[path] is null) throw new InvalidOperationException($"OpenAPI is missing {path}.");
});

await CheckAsync("capabilities and profile catalog", async () =>
{
    using var capabilities = await http.GetAsync("/v1/capabilities").ConfigureAwait(false);
    capabilities.EnsureSuccessStatusCode();
    var body = JsonNode.Parse(await capabilities.Content.ReadAsStringAsync().ConfigureAwait(false))?.ToJsonString() ?? string.Empty;
    foreach (var extension in capabilityExtensions)
        if (!body.Contains(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Capability catalog is missing {extension}.");

    using var profiles = await http.GetAsync("/v1/profiles").ConfigureAwait(false);
    profiles.EnsureSuccessStatusCode();
});

await CheckAsync("watermark profile CRUD and selection", async () =>
{
    var profileName = "release-check-" + Guid.NewGuid().ToString("N");
    using var save = await http.PutAsJsonAsync($"/v1/watermark-profiles/{profileName}", new
    {
        kind = "Text", text = "RELEASE CHECK", fontSize = 24, color = "#334455", opacity = .22,
        rotation = -20, position = "Center", layer = "Over", pages = "all", tile = false
    }).ConfigureAwait(false);
    save.EnsureSuccessStatusCode();
    using var get = await http.GetAsync($"/v1/watermark-profiles/{profileName}").ConfigureAwait(false);
    get.EnsureSuccessStatusCode();
    var body = await get.Content.ReadAsStringAsync().ConfigureAwait(false);
    if (!body.Contains("RELEASE CHECK", StringComparison.Ordinal)) throw new InvalidOperationException("Saved watermark profile could not be read back.");
    await ConvertSyncAsync(FindFixture("fixture-text-txt.txt"), profileName, null).ConfigureAwait(false);
    using var delete = await http.DeleteAsync($"/v1/watermark-profiles/{profileName}").ConfigureAwait(false);
    if (delete.StatusCode != HttpStatusCode.NoContent) throw new InvalidOperationException($"Profile delete returned {(int)delete.StatusCode}.");
});

var fixtureCases = new (string FileName, string Label)[]
{
    ("fixture-office-docx.docx", "DOCX to PDF"),
    ("fixture-office-doc.doc", "DOC to PDF"),
    ("fixture-spreadsheet-xlsx.xlsx", "XLSX to PDF"),
    ("fixture-spreadsheet-xls.xls", "XLS to PDF"),
    ("fixture-spreadsheet-ods.ods", "ODS to PDF"),
    ("fixture-data-csv.csv", "CSV to PDF"),
    ("fixture-office-odt.odt", "ODT to PDF"),
    ("fixture-office-template.dot", "DOT to PDF"),
    ("fixture-presentation-pptx.pptx", "PPTX to PDF"),
    ("fixture-presentation-odp.odp", "ODP to PDF"),
    ("fixture-presentation-ppt.ppt", "PPT to PDF"),
    ("fixture-text-rtf.rtf", "RTF to PDF"),
    ("fixture-web-html.html", "HTML to PDF"),
    ("fixture-text-txt.txt", "TXT to PDF"),
    ("fixture-image-png.png", "PNG to PDF"),
    ("fixture-image-jpeg.jpg", "JPEG to PDF"),
    ("fixture-image-gif.gif", "GIF to PDF"),
    ("fixture-image-bmp.bmp", "BMP to PDF"),
    ("fixture-image-tiff.tiff", "TIFF to PDF"),
    ("fixture-existing-pdf.pdf", "PDF pass-through")
};

foreach (var (fileName, label) in fixtureCases)
{
    await CheckAsync(label, async () => await ConvertSyncAsync(FindFixture(fileName), null, null).ConfigureAwait(false));
}

await CheckAsync("direct watermark options", async () =>
{
    await ConvertSyncAsync(FindFixture("fixture-text-txt.txt"), null,
        new { kind = "Text", text = "DIRECT", opacity = .3, rotation = 12, position = "BottomRight", layer = "Behind", pages = "1", tile = false }).ConfigureAwait(false);
});

Guid asyncJobId = Guid.Empty;
await CheckAsync("async polling and output download", async () =>
{
    using var content = CreateMultipart(FindFixture("fixture-text-txt.txt"), mode: "async", idempotencyKey: "release-check-" + Guid.NewGuid().ToString("N"));
    using var response = await http.PostAsync("/v1/conversions", content).ConfigureAwait(false);
    if (response.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException(await ResponseFailureAsync(response).ConfigureAwait(false));
    var accepted = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))?.AsObject()
        ?? throw new InvalidOperationException("Async response was not JSON.");
    asyncJobId = accepted["jobId"]?.GetValue<Guid>() ?? throw new InvalidOperationException("Async response did not contain jobId.");
    var status = await WaitForJobAsync(asyncJobId).ConfigureAwait(false);
    if (!string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Job ended in {status}.");
    using var output = await http.GetAsync($"/v1/jobs/{asyncJobId}/output").ConfigureAwait(false);
    await EnsurePdfAsync(output).ConfigureAwait(false);
});

await CheckAsync("idempotent async retry", async () =>
{
    var key = "release-check-idempotency-" + Guid.NewGuid().ToString("N");
    using var firstContent = CreateMultipart(FindFixture("fixture-text-txt.txt"), mode: "async", idempotencyKey: key);
    using var first = await http.PostAsync("/v1/conversions", firstContent).ConfigureAwait(false);
    if (first.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException(await ResponseFailureAsync(first).ConfigureAwait(false));
    var firstBody = JsonNode.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(false))?.AsObject();
    var firstId = firstBody?["jobId"]?.GetValue<Guid>() ?? throw new InvalidOperationException("First submission had no jobId.");
    using var secondContent = CreateMultipart(FindFixture("fixture-text-txt.txt"), mode: "async", idempotencyKey: key);
    using var second = await http.PostAsync("/v1/conversions", secondContent).ConfigureAwait(false);
    if (second.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException(await ResponseFailureAsync(second).ConfigureAwait(false));
    var secondBody = JsonNode.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(false))?.AsObject();
    if (secondBody?["jobId"]?.GetValue<Guid>() != firstId) throw new InvalidOperationException("Idempotent retry created a different job.");
    await WaitForJobAsync(firstId).ConfigureAwait(false);
});

await CheckAsync("concurrent conversions", async () =>
{
    var tasks = Enumerable.Range(1, 4).Select(async index =>
    {
        var path = FindFixture("fixture-text-txt.txt");
        await ConvertSyncAsync(path, null, new { kind = "Text", text = $"CONCURRENT {index}", opacity = .15, pages = "all" }).ConfigureAwait(false);
    });
    await Task.WhenAll(tasks).ConfigureAwait(false);
});

if (options.WatchRoot is not null)
{
    await CheckAsync("watch-folder conversion and output", async () =>
    {
        var input = Path.Combine(options.WatchRoot, "Input");
        var output = Path.Combine(options.WatchRoot, "Output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var name = "release-check-" + Guid.NewGuid().ToString("N") + ".txt";
        File.Copy(FindFixture("fixture-text-txt.txt"), Path.Combine(input, name));
        var deadline = DateTimeOffset.UtcNow + options.Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Directory.EnumerateFiles(output, "*.pdf", SearchOption.TopDirectoryOnly).Any(path => Path.GetFileNameWithoutExtension(path).StartsWith(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)))
                return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        throw new TimeoutException("The watch-folder output did not appear before the release-check timeout.");
    });
}

Console.WriteLine();
Console.WriteLine(success ? $"Release checks passed: {checks.Count}." : $"Release checks failed: {failures.Count}.");
if (!success)
    foreach (var failure in failures) Console.WriteLine($"- {failure}");
return success ? 0 : 1;

string FindFixture(string fileName)
{
    var path = Path.Combine(options.FixtureRoot, fileName);
    if (!File.Exists(path)) throw new FileNotFoundException($"Required release fixture is missing: {fileName}.", path);
    return path;
}

async Task ConvertSyncAsync(string path, string? watermarkProfile, object? directWatermark)
{
    using var content = CreateMultipart(path, mode: "sync", watermarkProfile: watermarkProfile, directWatermark: directWatermark);
    using var response = await http.PostAsync("/v1/conversions", content).ConfigureAwait(false);
    await EnsurePdfAsync(response).ConfigureAwait(false);
}

MultipartFormDataContent CreateMultipart(string path, string mode, string? watermarkProfile = null, object? directWatermark = null, string? idempotencyKey = null)
{
    var content = new MultipartFormDataContent();
    content.Add(new StringContent(mode), "mode");
    content.Add(new StringContent("archive"), "profile");
    if (!string.IsNullOrWhiteSpace(watermarkProfile)) content.Add(new StringContent(watermarkProfile), "watermarkProfile");
    if (directWatermark is not null) content.Add(new StringContent(JsonSerializer.Serialize(directWatermark), Encoding.UTF8, "application/json"), "watermark");
    if (!string.IsNullOrWhiteSpace(idempotencyKey)) content.Headers.Add("Idempotency-Key", idempotencyKey);
    var file = new StreamContent(File.OpenRead(path));
    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    content.Add(file, "file", Path.GetFileName(path));
    return content;
}

async Task<string> WaitForJobAsync(Guid jobId)
{
    var deadline = DateTimeOffset.UtcNow + options.Timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        using var response = await http.GetAsync($"/v1/jobs/{jobId:D}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))?.AsObject();
        var state = body?["state"]?.GetValue<string>() ?? throw new InvalidOperationException("Job status did not contain state.");
        if (state is "Succeeded" or "Failed" or "DeadLettered" or "Cancelled") return state;
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }
    throw new TimeoutException($"Job {jobId:D} did not reach a terminal state.");
}

async Task EnsurePdfAsync(HttpResponseMessage response)
{
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ResponseFailureAsync(response).ConfigureAwait(false));
    if (response.Content.Headers.ContentType?.MediaType?.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) != true)
        throw new InvalidOperationException("Conversion response did not have application/pdf content type.");
    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-") throw new InvalidOperationException("Conversion response was not a PDF.");
}

async Task ExpectStatusAsync(string path, HttpStatusCode expected)
{
    using var response = await http.GetAsync(path).ConfigureAwait(false);
    if (response.StatusCode != expected) throw new InvalidOperationException(await ResponseFailureAsync(response).ConfigureAwait(false));
}

static async Task<string> ResponseFailureAsync(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    return $"HTTP {(int)response.StatusCode} {response.StatusCode}: {body[..Math.Min(body.Length, 400)]}";
}

sealed record ReleaseOptions(Uri BaseUri, string FixtureRoot, string? WatchRoot, string? Token, TimeSpan Timeout)
{
    public static ReleaseOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{args[index]}'.");
            var name = args[index][2..];
            if (name is "help" or "h") { PrintHelp(); Environment.Exit(0); }
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for --{name}.");
            values[name] = args[++index];
        }

        var baseUrl = Get(values, "base-url", "INACTIVEPDF_RELEASE_BASE_URL") ?? "http://127.0.0.1:5080/";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var fixtureRoot = Get(values, "fixture-root", "INACTIVEPDF_RELEASE_FIXTURE_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "fixtures", "TestFixtures");
        var watchRoot = Get(values, "watch-root", "INACTIVEPDF_RELEASE_WATCH_ROOT");
        var token = Get(values, "token", "INACTIVEPDF_API_TOKEN");
        var timeoutSeconds = int.TryParse(Get(values, "timeout-seconds", "INACTIVEPDF_RELEASE_TIMEOUT_SECONDS"), out var value) && value > 0 ? value : 120;
        return new ReleaseOptions(new Uri(baseUrl, UriKind.Absolute), Path.GetFullPath(fixtureRoot), string.IsNullOrWhiteSpace(watchRoot) ? null : Path.GetFullPath(watchRoot), token, TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string? Get(Dictionary<string, string?> values, string argument, string environment) => values.TryGetValue(argument, out var value) ? value : Environment.GetEnvironmentVariable(environment);

    private static void PrintHelp() => Console.WriteLine("InactivePDF.ReleaseCheck --base-url URL --fixture-root PATH [--watch-root PATH] [--token TOKEN] [--timeout-seconds N]");
}
