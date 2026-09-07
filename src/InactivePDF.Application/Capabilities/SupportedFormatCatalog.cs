using System.Collections.Frozen;

namespace InactivePDF.Application.Capabilities;

public enum ConversionFormatCategory
{
    Pdf,
    Image,
    PlainText,
    Html,
    Rtf,
    Office
}

public enum ConversionFormatRoute
{
    PdfPassThrough,
    Image,
    TextGenerator,
    LibreOffice
}

public sealed record SupportedFormatDescriptor(
    string Extension,
    ConversionFormatCategory Category,
    ConversionFormatRoute Route,
    IReadOnlyList<string> ContentTypes,
    bool SupportsVisualLayout,
    bool SupportsLosslessPassThrough);

/// <summary>
/// The only source of truth for advertised formats, validation, routing, watch-folder
/// classification, and API capability output. Extensions are canonicalized case-insensitively.
/// </summary>
public static class SupportedFormatCatalog
{
    private static readonly SupportedFormatDescriptor[] Definitions =
    [
        Format(".pdf", ConversionFormatCategory.Pdf, ConversionFormatRoute.PdfPassThrough, ["application/pdf"], true, true),
        Format(".bmp", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/bmp", "image/x-ms-bmp"], true, false),
        Format(".gif", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/gif"], true, false),
        Format(".jpeg", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/jpeg"], true, false),
        Format(".jpg", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/jpeg"], true, false),
        Format(".png", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/png"], true, false),
        Format(".tif", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/tiff"], true, false),
        Format(".tiff", ConversionFormatCategory.Image, ConversionFormatRoute.Image, ["image/tiff"], true, false),
        Format(".txt", ConversionFormatCategory.PlainText, ConversionFormatRoute.TextGenerator, ["text/plain"], false, false),
        Format(".csv", ConversionFormatCategory.PlainText, ConversionFormatRoute.LibreOffice, ["text/csv", "application/csv", "application/vnd.ms-excel"], true, false),
        Format(".html", ConversionFormatCategory.Html, ConversionFormatRoute.LibreOffice, ["text/html", "application/xhtml+xml"], true, false),
        Format(".htm", ConversionFormatCategory.Html, ConversionFormatRoute.LibreOffice, ["text/html", "application/xhtml+xml"], true, false),
        Format(".rtf", ConversionFormatCategory.Rtf, ConversionFormatRoute.LibreOffice, ["application/rtf", "text/rtf"], true, false),
        Format(".doc", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/msword"], true, false),
        Format(".docx", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"], true, false),
        Format(".dot", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/msword", "application/vnd.ms-word"], true, false),
        Format(".odt", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.oasis.opendocument.text"], true, false),
        Format(".odp", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.oasis.opendocument.presentation"], true, false),
        Format(".ods", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.oasis.opendocument.spreadsheet"], true, false),
        Format(".xls", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.ms-excel"], true, false),
        Format(".xlsx", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"], true, false),
        Format(".ppt", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.ms-powerpoint"], true, false),
        Format(".pptx", ConversionFormatCategory.Office, ConversionFormatRoute.LibreOffice, ["application/vnd.openxmlformats-officedocument.presentationml.presentation"], true, false)
    ];

    private static readonly FrozenDictionary<string, SupportedFormatDescriptor> ByExtension =
        Definitions.ToFrozenDictionary(format => format.Extension, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SupportedFormatDescriptor> All { get; } = Definitions;

    public static bool IsSupported(string extension) => TryGet(extension, out _);

    public static bool IsImage(string extension) => TryGet(extension, out var format) && format.Category == ConversionFormatCategory.Image;

    public static bool TryGet(string extension, out SupportedFormatDescriptor format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return ByExtension.TryGetValue(NormalizeExtension(extension), out format!);
    }

    public static SupportedFormatDescriptor GetRequired(string extension) =>
        TryGet(extension, out var format)
            ? format
            : throw new NotSupportedException($"Unsupported file extension '{extension}'.");

    public static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.Trim();
        return normalized.Length > 0 && normalized[0] == '.' ? normalized.ToLowerInvariant() : $".{normalized.ToLowerInvariant()}";
    }

    private static SupportedFormatDescriptor Format(
        string extension,
        ConversionFormatCategory category,
        ConversionFormatRoute route,
        IReadOnlyList<string> contentTypes,
        bool supportsVisualLayout,
        bool supportsLosslessPassThrough) =>
        new(extension, category, route, contentTypes, supportsVisualLayout, supportsLosslessPassThrough);
}
