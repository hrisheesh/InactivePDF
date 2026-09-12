using InactivePDF.Client;

// Add a project reference to src/InactivePDF.Client/InactivePDF.Client.csproj.
using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("INACTIVEPDF_URL") ?? "http://127.0.0.1:5080/") };
http.DefaultRequestHeaders.Authorization = new("Bearer", Environment.GetEnvironmentVariable("INACTIVEPDF_TOKEN") ?? throw new InvalidOperationException("Set INACTIVEPDF_TOKEN."));
using var client = new InactivePdfClient(http);
await using var input = File.OpenRead(args.Length == 0 ? "document.docx" : args[0]);
var accepted = await client.ConvertAsync(new InactivePdfConversionRequest(input, Path.GetFileName(input.Name))
{
    Mode = InactivePdfConversionMode.Async,
    Profile = "archive",
    IdempotencyKey = "csharp-example-" + Guid.NewGuid().ToString("N")
});
var completed = await client.WaitForCompletionAsync(accepted.Job!.JobId);
await using var output = File.Create("converted.pdf");
await client.DownloadJobOutputAsync(completed.JobId, output);
Console.WriteLine($"Converted {completed.JobId} to converted.pdf");
