using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.IO;

public sealed class PdfSharpOperations : IPdfOperations
{
    public PdfInspection Inspect(string filePath)
    {
        EnsureExistingPdfPath(filePath);

        using var document = OpenPdf(filePath, PdfDocumentOpenMode.Import);
        var fileInfo = new FileInfo(filePath);
        return new PdfInspection(
            fileInfo.FullName,
            fileInfo.Length,
            document.PageCount,
            document.SecuritySettings.IsEncrypted,
            document.Version,
            document.Info.Creator);
    }

    public void Merge(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (inputPaths.Count == 0) throw new ArgumentException("At least one PDF is required.", nameof(inputPaths));
        var normalizedOutputPath = EnsureOutputPdfPath(outputPath);
        var resolvedProfile = profile ?? PdfOutputProfileCatalog.Resolve("archive");
        var normalizedInputs = inputPaths.Select(EnsureExistingPdfPath).ToArray();
        if (normalizedInputs.Any(input => PathsEqual(input, normalizedOutputPath)))
        {
            throw new ArgumentException("The output PDF must be different from every input PDF.", nameof(outputPath));
        }

        var temporaryOutputPath = Path.Combine(
            Path.GetDirectoryName(normalizedOutputPath)!,
            $".{Path.GetFileNameWithoutExtension(normalizedOutputPath)}.{Guid.NewGuid():N}.tmp.pdf");

        var output = new PdfDocument();
        var importedDocuments = new List<PdfDocument>(normalizedInputs.Length);
        try
        {
            foreach (var inputPath in normalizedInputs)
            {
                var imported = OpenPdf(inputPath, PdfDocumentOpenMode.Import);
                importedDocuments.Add(imported);
                foreach (var page in imported.Pages)
                {
                    output.AddPage(page);
                }
            }

            PdfProfileDocumentConfigurator.Apply(output, resolvedProfile);
            output.Save(temporaryOutputPath);
            _ = Inspect(temporaryOutputPath);
            File.Move(temporaryOutputPath, normalizedOutputPath, overwrite: true);
        }
        finally
        {
            output.Dispose();
            foreach (var imported in importedDocuments)
            {
                imported.Dispose();
            }

            if (File.Exists(temporaryOutputPath)) File.Delete(temporaryOutputPath);
        }
    }

    public void ApplyProfile(string filePath, PdfOutputProfile profile)
    {
        EnsureExistingPdfPath(filePath);
        ArgumentNullException.ThrowIfNull(profile);
        var normalizedPath = Path.GetFullPath(filePath);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(normalizedPath)!,
            $".{Path.GetFileNameWithoutExtension(normalizedPath)}.{Guid.NewGuid():N}.profile.pdf");
        try
        {
            using (var document = OpenPdf(normalizedPath, PdfDocumentOpenMode.Modify))
            {
                PdfProfileDocumentConfigurator.Apply(document, profile);
                document.Save(temporaryPath);
            }

            ReplaceAtomically(temporaryPath, normalizedPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string EnsureExistingPdfPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The PDF path does not exist or is not a PDF path.", path);
        }

        return fullPath;
    }

    private static string EnsureOutputPdfPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output path must have a .pdf extension.", nameof(path));
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        return fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static PdfDocument OpenPdf(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException exception) when (LooksEncrypted(exception))
        {
            throw new ConversionFormatException(
                "encrypted_input",
                $"The PDF '{Path.GetFileName(path)}' is encrypted or password protected. InactivePDF does not accept password protected PDF inputs.",
                exception);
        }
        catch (PdfReaderException exception)
        {
            throw new ConversionFormatException(
                "invalid_pdf",
                $"The PDF '{Path.GetFileName(path)}' could not be read or is structurally invalid: {exception.Message}",
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            throw new ConversionFormatException(
                "invalid_pdf",
                $"The PDF '{Path.GetFileName(path)}' could not be read or is structurally invalid: {exception.Message}",
                exception);
        }
    }

    private static bool LooksEncrypted(PdfReaderException exception) =>
        exception.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("security", StringComparison.OrdinalIgnoreCase);

    private static void ReplaceAtomically(string source, string destination)
    {
        try
        {
            File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(source, destination, overwrite: true);
        }
    }
}
