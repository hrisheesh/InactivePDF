using System.IO.Compression;
using System.Text;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Infrastructure.Validation;

public sealed record InputFormatValidation(
    SupportedFormatDescriptor Format,
    string DetectedContentType,
    string Signature);

/// <summary>
/// Validates the declared filename, optional content type, and file signature before routing.
/// Generic multipart types such as application/octet-stream are deliberately accepted because
/// legacy callers often do not provide a MIME type. Development scans text-like inputs fully;
/// Production validates a bounded sample and leaves the required conversion read to the engine.
/// </summary>
public static class InputFormatValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static InputFormatValidation Validate(
        string path,
        string fileName,
        string? declaredContentType = null,
        ConversionExecutionMode executionMode = ConversionExecutionMode.Development)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The input file does not exist.", fullPath);

        var extension = Path.GetExtension(Path.GetFileName(fileName));
        if (!SupportedFormatCatalog.TryGet(extension, out var format))
        {
            throw new ConversionFormatException(
                "unsupported_extension",
                $"The extension '{extension}' is not supported. The declared filename was '{Path.GetFileName(fileName)}'.");
        }

        ValidateContentType(format, declaredContentType);
        var signature = DetectSignature(fullPath, format, executionMode);
        return new InputFormatValidation(format, signature.ContentType, signature.Name);
    }

    private static void ValidateContentType(SupportedFormatDescriptor format, string? declaredContentType)
    {
        var normalized = NormalizeContentType(declaredContentType);
        if (normalized is null || normalized is "application/octet-stream" or "binary/octet-stream") return;
        if (!format.ContentTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new ConversionFormatException(
                "content_type_mismatch",
                $"The content type '{normalized}' does not match '{format.Extension}'. Expected: {string.Join(", ", format.ContentTypes)}.");
        }
    }

    private static (string Name, string ContentType) DetectSignature(string path, SupportedFormatDescriptor format, ConversionExecutionMode executionMode)
    {
        var header = new byte[512];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, header.Length, FileOptions.SequentialScan))
        {
            _ = stream.Read(header, 0, header.Length);
        }

        var signature = DetectBinarySignature(header);
        if (signature is not null && !MatchesFormat(signature.Value.Name, format))
        {
            throw new ConversionFormatException(
                "signature_mismatch",
                $"The file named '{Path.GetFileName(path)}' has signature '{signature.Value.Name}', which does not match '{format.Extension}'.");
        }

        if (signature is null && RequiresBinarySignature(format))
        {
            throw new ConversionFormatException(
                "signature_missing",
                $"The file named '{Path.GetFileName(path)}' does not contain the expected '{format.Extension}' file signature.");
        }

        if (format.Category is ConversionFormatCategory.PlainText or ConversionFormatCategory.Html or ConversionFormatCategory.Rtf)
            ValidateTextLikeFile(path, format, executionMode);
        else if (format.Route == ConversionFormatRoute.LibreOffice && IsZipContainer(format))
            ValidateZipContainer(path, format);

        return signature ?? (format.Category switch
        {
            ConversionFormatCategory.Html => ("html-text", "text/html"),
            ConversionFormatCategory.Rtf => ("rtf-text", "application/rtf"),
            ConversionFormatCategory.PlainText => ("text", format.ContentTypes[0]),
            _ => ("declared-extension", format.ContentTypes[0])
        });
    }

    private static void ValidateTextLikeFile(string path, SupportedFormatDescriptor format, ConversionExecutionMode executionMode)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            var sampleBuilder = new StringBuilder(capacity: 8 * 1024);
            var buffer = new char[64 * 1024];
            var fullValidation = executionMode == ConversionExecutionMode.Development;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (sampleBuilder.Length < 1_048_576)
                    sampleBuilder.Append(buffer, 0, Math.Min(read, 1_048_576 - sampleBuilder.Length));
                if (!fullValidation && sampleBuilder.Length >= 1_048_576) break;
            }

            var sample = sampleBuilder.ToString();
            if (format.Category == ConversionFormatCategory.Rtf && !sample.TrimStart().StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
                throw new ConversionFormatException("signature_mismatch", "The file is declared as RTF but does not begin with an RTF header.");
            if (format.Category == ConversionFormatCategory.Html && !LooksLikeHtml(sample))
                throw new ConversionFormatException("signature_mismatch", "The file is declared as HTML but contains no recognizable HTML markup.");
        }
        catch (DecoderFallbackException)
        {
            throw new ConversionFormatException("invalid_text_encoding", "The text input is not valid UTF-8 or UTF-16.");
        }
    }

    private static bool LooksLikeHtml(string value) =>
        value.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<p", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<div", StringComparison.OrdinalIgnoreCase);

    private static void ValidateZipContainer(string path, SupportedFormatDescriptor format)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
                throw new ConversionFormatException("invalid_package", $"The '{format.Extension}' package contains no entries.");

            if (format.Extension is ".odt" or ".ods" or ".odp")
            {
                var mimetype = archive.GetEntry("mimetype")
                    ?? throw new ConversionFormatException("invalid_package", $"The '{format.Extension}' package does not contain the required mimetype entry.");
                using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
                var declared = reader.ReadToEnd().Trim();
                var expected = format.Extension switch
                {
                    ".odt" => "application/vnd.oasis.opendocument.text",
                    ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
                    _ => "application/vnd.oasis.opendocument.presentation"
                };
                if (!string.Equals(declared, expected, StringComparison.Ordinal))
                    throw new ConversionFormatException("package_type_mismatch", $"The '{format.Extension}' package declares mimetype '{declared}', expected '{expected}'.");
            }
        }
        catch (ConversionFormatException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw new ConversionFormatException("invalid_package", $"The '{format.Extension}' package is not a valid ZIP container.");
        }
    }

    private static bool IsZipContainer(SupportedFormatDescriptor format) => format.Extension is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp";

    private static bool RequiresBinarySignature(SupportedFormatDescriptor format) =>
        format.Category is ConversionFormatCategory.Pdf or ConversionFormatCategory.Image ||
        IsZipContainer(format) || format.Extension is ".doc" or ".dot" or ".xls" or ".ppt";

    private static bool MatchesFormat(string signature, SupportedFormatDescriptor format) => signature switch
    {
        "pdf" => format.Category == ConversionFormatCategory.Pdf,
        "png" => format.Extension == ".png",
        "jpeg" => format.Extension is ".jpg" or ".jpeg",
        "gif" => format.Extension == ".gif",
        "bmp" => format.Extension == ".bmp",
        "tiff" => format.Extension is ".tif" or ".tiff",
        "ole" => format.Extension is ".doc" or ".dot" or ".xls" or ".ppt",
        "zip" => IsZipContainer(format),
        "rtf" => format.Category == ConversionFormatCategory.Rtf,
        _ => true
    };

    private static (string Name, string ContentType)? DetectBinarySignature(byte[] header)
    {
        if (header.AsSpan().StartsWith("%PDF-"u8)) return ("pdf", "application/pdf");
        if (header.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("png", "image/png");
        if (header.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return ("jpeg", "image/jpeg");
        if (header.AsSpan().StartsWith("GIF87a"u8) || header.AsSpan().StartsWith("GIF89a"u8)) return ("gif", "image/gif");
        if (header.AsSpan().StartsWith("BM"u8)) return ("bmp", "image/bmp");
        if (header.AsSpan().StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || header.AsSpan().StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })) return ("tiff", "image/tiff");
        if (header.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return ("ole", "application/octet-stream");
        if (header.AsSpan().StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) return ("zip", "application/zip");
        var text = Encoding.ASCII.GetString(header).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text.StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase)) return ("rtf", "application/rtf");
        return null;
    }

    private static string? NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf(';');
        return (separator < 0 ? value : value[..separator]).Trim().ToLowerInvariant();
    }
}
