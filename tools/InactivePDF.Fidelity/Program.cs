using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageMagick;
using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Infrastructure.IO;

InactivePdfSettings.LoadAndApply();

var arguments = ParseArguments(args);
if (!arguments.TryGetValue("reference", out var reference) || !arguments.TryGetValue("candidate", out var candidate))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/InactivePDF.Fidelity -- --reference reference.pdf --candidate candidate.pdf [--output report.json] [--fail-on-difference]");
    return 2;
}

var report = await CompareAsync(reference, candidate, arguments.TryGetValue("output", out var output) ? output : null);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (output is null) Console.WriteLine(json);
else await File.WriteAllTextAsync(output, json + Environment.NewLine);

var failed = !report.PageCountEqual || report.Text is { Available: true, Equal: false } || report.Visual is { Available: true, Equal: false };
return failed && arguments.ContainsKey("fail-on-difference") ? 1 : 0;

static async Task<FidelityReport> CompareAsync(string referencePath, string candidatePath, string? outputPath)
{
    var reference = Path.GetFullPath(referencePath);
    var candidate = Path.GetFullPath(candidatePath);
    var referenceInfo = File.Exists(reference) ? new FileInfo(reference) : null;
    var candidateInfo = File.Exists(candidate) ? new FileInfo(candidate) : null;
    var referenceInspection = referenceInfo is null ? null : new PdfSharpOperations().Inspect(reference);
    var candidateInspection = candidateInfo is null ? null : new PdfSharpOperations().Inspect(candidate);
    var text = await CompareTextAsync(reference, candidate);
    var visual = await CompareVisualAsync(reference, candidate);

    return new FidelityReport(
        DateTimeOffset.UtcNow,
        reference,
        candidate,
        outputPath,
        referenceInfo is not null && candidateInfo is not null &&
        string.Equals(await Sha256Async(reference), await Sha256Async(candidate), StringComparison.OrdinalIgnoreCase),
        referenceInfo?.Length ?? 0,
        candidateInfo?.Length ?? 0,
        referenceInspection?.PageCount ?? 0,
        candidateInspection?.PageCount ?? 0,
        referenceInspection?.IsEncrypted ?? false,
        candidateInspection?.IsEncrypted ?? false,
        referenceInspection is not null && candidateInspection is not null && referenceInspection.PageCount == candidateInspection.PageCount,
        text,
        visual);
}

static async Task<TextComparison> CompareTextAsync(string reference, string candidate)
{
    var referenceText = await ExtractTextAsync(reference);
    var candidateText = await ExtractTextAsync(candidate);
    if (!referenceText.Available || !candidateText.Available)
        return new TextComparison(false, false, referenceText.Error ?? candidateText.Error);

    var normalizedReference = NormalizeText(referenceText.Text!);
    var normalizedCandidate = NormalizeText(candidateText.Text!);
    return new TextComparison(true, string.Equals(normalizedReference, normalizedCandidate, StringComparison.Ordinal), null);
}

static async Task<ExternalTextResult> ExtractTextAsync(string path)
{
    var tool = Environment.GetEnvironmentVariable("INACTIVEPDF_PDFTOTEXT_PATH") ?? "pdftotext";
    var result = await RunToolAsync(tool, ["-layout", path, "-"], TimeSpan.FromMinutes(2));
    return result.Started && result.ExitCode == 0
        ? new ExternalTextResult(true, result.StandardOutput, null)
        : new ExternalTextResult(false, null, result.Error ?? result.StandardError);
}

static async Task<VisualComparison> CompareVisualAsync(string reference, string candidate)
{
    var referenceDirectory = Path.Combine(Path.GetTempPath(), "InactivePDF", "fidelity", Guid.NewGuid().ToString("N"));
    var candidateDirectory = Path.Combine(Path.GetTempPath(), "InactivePDF", "fidelity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(referenceDirectory);
    Directory.CreateDirectory(candidateDirectory);
    try
    {
        var referenceRender = await RenderAsync(reference, referenceDirectory);
        var candidateRender = await RenderAsync(candidate, candidateDirectory);
        if (!referenceRender.Available || !candidateRender.Available)
            return new VisualComparison(false, false, 0, 0, 0, referenceRender.Error ?? candidateRender.Error);

        var referencePages = Directory.EnumerateFiles(referenceDirectory, "*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var candidatePages = Directory.EnumerateFiles(candidateDirectory, "*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var pageCount = Math.Min(referencePages.Length, candidatePages.Length);
        var differentPages = referencePages.Length == candidatePages.Length ? 0 : Math.Abs(referencePages.Length - candidatePages.Length);
        double totalError = 0;
        long comparedPixels = 0;
        for (var index = 0; index < pageCount; index++)
        {
            var comparison = CompareImages(referencePages[index], candidatePages[index]);
            if (!comparison.Equal) differentPages++;
            totalError += comparison.MeanAbsoluteError * comparison.Pixels;
            comparedPixels += comparison.Pixels;
        }

        var meanError = comparedPixels == 0 ? 0 : totalError / comparedPixels;
        return new VisualComparison(true, differentPages == 0, pageCount, differentPages, meanError, null);
    }
    finally
    {
        TryDelete(referenceDirectory);
        TryDelete(candidateDirectory);
    }
}

static async Task<ExternalRenderResult> RenderAsync(string path, string outputDirectory)
{
    var tool = Environment.GetEnvironmentVariable("INACTIVEPDF_PDFTOPPM_PATH") ?? "pdftoppm";
    var prefix = Path.Combine(outputDirectory, "page");
    var result = await RunToolAsync(tool, ["-png", "-r", "144", path, prefix], TimeSpan.FromMinutes(5));
    return result.Started && result.ExitCode == 0
        ? new ExternalRenderResult(true, null)
        : new ExternalRenderResult(false, result.Error ?? result.StandardError);
}

static ImageComparison CompareImages(string referencePath, string candidatePath)
{
    using var reference = new MagickImage(referencePath);
    using var candidate = new MagickImage(candidatePath);
    if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        return new ImageComparison(false, 0, 255);

    var referencePixels = reference.ToByteArray(MagickFormat.Rgba);
    var candidatePixels = candidate.ToByteArray(MagickFormat.Rgba);
    long pixels = 0;
    double error = 0;
    for (var index = 0; index < referencePixels.Length; index++)
    {
        error += Math.Abs(referencePixels[index] - candidatePixels[index]);
        if ((index + 1) % 4 == 0) pixels++;
    }

    var mean = error / (pixels * 4d);
    return new ImageComparison(mean < 0.5, pixels, mean);
}

static async Task<string> Sha256Async(string path)
{
    await using var stream = File.OpenRead(path);
    using var hash = SHA256.Create();
    return Convert.ToHexString(await hash.ComputeHashAsync(stream));
}

static string NormalizeText(string value) => Regex.Replace(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), "\\s+", " ").Trim();

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = args[index][2..];
        if (key == "fail-on-difference") result[key] = string.Empty;
        else if (index + 1 < args.Length) result[key] = args[++index];
    }

    return result;
}

static async Task<ExternalCommandResult> RunToolAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    try
    {
        if (!process.Start()) return new ExternalCommandResult(false, -1, string.Empty, string.Empty, "The comparison tool did not start.");
    }
    catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
    {
        return new ExternalCommandResult(false, -1, string.Empty, string.Empty, $"Comparison tool '{executable}' was not found.");
    }

    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    using var cancellation = new CancellationTokenSource(timeout);
    try { await process.WaitForExitAsync(cancellation.Token); }
    catch (OperationCanceledException)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        return new ExternalCommandResult(true, -1, await standardOutput, await standardError, $"Comparison tool exceeded {timeout}.");
    }

    return new ExternalCommandResult(true, process.ExitCode, await standardOutput, await standardError, null);
}

static void TryDelete(string path)
{
    try { if (Directory.Exists(path)) Directory.Delete(path, true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

sealed record ExternalTextResult(bool Available, string? Text, string? Error);
sealed record ExternalRenderResult(bool Available, string? Error);
sealed record ExternalCommandResult(bool Started, int ExitCode, string StandardOutput, string StandardError, string? Error);
sealed record ImageComparison(bool Equal, long Pixels, double MeanAbsoluteError);
sealed record TextComparison(bool Available, bool Equal, string? Error);
sealed record VisualComparison(bool Available, bool Equal, int PagesCompared, int DifferentPages, double MeanAbsoluteError, string? Error);
sealed record FidelityReport(
    DateTimeOffset ComparedUtc,
    string ReferencePath,
    string CandidatePath,
    string? ReportPath,
    bool ByteIdentical,
    long ReferenceBytes,
    long CandidateBytes,
    int ReferencePages,
    int CandidatePages,
    bool ReferenceEncrypted,
    bool CandidateEncrypted,
    bool PageCountEqual,
    TextComparison Text,
    VisualComparison Visual);
