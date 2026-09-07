using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Validation;

namespace InactivePDF.Infrastructure.Processes;

public sealed class LibreOfficeConverter(LibreOfficeOptions options) : IOfficeDocumentConverter
{
    public Task<OfficeConversionResult> ConvertAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        ConvertAsync(inputPath, outputPath, PdfOutputProfileCatalog.Resolve("archive"), cancellationToken);

    public async Task<OfficeConversionResult> ConvertAsync(string inputPath, string outputPath, PdfOutputProfile profile, CancellationToken cancellationToken = default)
    {
        var input = ValidateInput(inputPath);
        var output = ValidateOutput(outputPath);
        if (!File.Exists(options.ExecutablePath) && !CanResolveExecutable(options.ExecutablePath))
            throw new FileNotFoundException("LibreOffice executable was not found.", options.ExecutablePath);

        var workingDirectory = Path.Combine(Path.GetDirectoryName(output)!, $".lo-{Guid.NewGuid():N}");
        var ownsProfile = string.IsNullOrWhiteSpace(options.SharedProfilePath);
        var profileDirectory = ownsProfile ? Path.Combine(workingDirectory, "profile") : options.SharedProfilePath!;
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(profileDirectory);
        if (ownsProfile) LibreOfficeProfileProvisioner.Create(profileDirectory);
        var started = Stopwatch.StartNew();
        try
        {
            var startInfo = BuildStartInfo(input, workingDirectory, profileDirectory, profile);
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("LibreOffice failed to start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ConversionTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Terminate(process);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Terminate(process);
                var timeoutOutput = await standardOutput.ConfigureAwait(false);
                var timeoutError = await standardError.ConfigureAwait(false);
                throw new TimeoutException($"LibreOffice conversion exceeded {options.ConversionTimeout}. ProcessId={process.Id}. StandardOutput={timeoutOutput.Trim()} StandardError={timeoutError.Trim()}");
            }

            var outputText = await standardOutput.ConfigureAwait(false);
            var errorText = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"LibreOffice exited with code {process.ExitCode}. StandardOutput={outputText.Trim()} StandardError={errorText.Trim()}");

            var generated = Path.Combine(workingDirectory, Path.GetFileNameWithoutExtension(input) + ".pdf");
            if (!File.Exists(generated))
                throw new InvalidOperationException($"LibreOffice completed without producing a PDF. StandardOutput={outputText.Trim()} StandardError={errorText.Trim()}");
            File.Move(generated, output, overwrite: true);
            return new OfficeConversionResult(output, new FileInfo(output).Length, started.Elapsed, process.ExitCode);
        }
        finally
        {
            if (ownsProfile) TryDeleteDirectory(workingDirectory);
        }
    }

    private ProcessStartInfo BuildStartInfo(string input, string outputDirectory, string profile, PdfOutputProfile pdfProfile)
    {
        var filter = ResolvePdfExportFilter(Path.GetExtension(input));
        var exportOptions = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SelectPdfVersion"] = new { type = "long", value = ResolveLibreOfficePdfVersion(pdfProfile.PdfVersion) },
            ["UseLosslessCompression"] = new { type = "boolean", value = pdfProfile.PreserveJpegData && !pdfProfile.DownsampleImages },
            ["Quality"] = new { type = "long", value = Math.Clamp(pdfProfile.JpegQuality, 1, 100) },
            ["ReduceImageResolution"] = new { type = "boolean", value = pdfProfile.DownsampleImages },
            ["MaxImageResolution"] = new { type = "long", value = ResolveLibreOfficeDpi(pdfProfile.MaximumImageDpi) },
            ["EmbedStandardFonts"] = new { type = "boolean", value = pdfProfile.FontPolicy.Contains("embed", StringComparison.OrdinalIgnoreCase) }
        });
        var arguments = $"--headless --invisible --nodefault --nologo --nolockcheck --norestore --nofirststartwizard -env:UserInstallation={ToFileUri(profile)} --convert-to {Quote($"pdf:{filter}:{exportOptions}")} --outdir {Quote(outputDirectory)} {Quote(input)}";
        if (!string.IsNullOrWhiteSpace(options.AdditionalArguments)) arguments = $"{options.AdditionalArguments} {arguments}";
        return new ProcessStartInfo(options.ExecutablePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = outputDirectory
        };
    }

    private static string ResolvePdfExportFilter(string extension) => extension.ToLowerInvariant() switch
    {
        ".xls" or ".xlsx" or ".ods" or ".csv" => "calc_pdf_Export",
        ".ppt" or ".pptx" or ".odp" => "impress_pdf_Export",
        _ => "writer_pdf_Export"
    };

    private static int ResolveLibreOfficePdfVersion(int version) => version switch
    {
        15 or 16 or 17 => version,
        _ => 17
    };

    private static int ResolveLibreOfficeDpi(int dpi)
    {
        if (dpi <= 0) return 300;
        var supported = new[] { 75, 150, 300, 600, 1200 };
        return supported.OrderBy(value => Math.Abs(value - dpi)).First();
    }

    private static string ValidateInput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The Office document does not exist.", path);
        var format = SupportedFormatCatalog.GetRequired(Path.GetExtension(fullPath));
        if (format.Route != ConversionFormatRoute.LibreOffice && format.Category != ConversionFormatCategory.PlainText)
            throw new NotSupportedException($"The LibreOffice route does not support extension: {Path.GetExtension(fullPath)}");
        _ = InputFormatValidator.Validate(fullPath, Path.GetFileName(fullPath));
        return fullPath;
    }

    private static string ValidateOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output path must have a .pdf extension.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static string ToFileUri(string path) => new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static bool CanResolveExecutable(string path) => !Path.IsPathRooted(path) && Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Any(directory => File.Exists(Path.Combine(directory, path))) is true;
    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
    }
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
