using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Capabilities;

public static class PdfOutputProfileCatalog
{
    private static readonly IReadOnlyList<PdfOutputProfile> Definitions = CreateDefinitions();

    private static IReadOnlyList<PdfOutputProfile> CreateDefinitions() =>
    [
        new PdfOutputProfile(
            "archive",
            PdfVersion: ReadInt("ARCHIVE", "PDF_VERSION", 17),
            PreserveJpegData: ReadBool("ARCHIVE", "PRESERVE_JPEG_DATA", true),
            DownsampleImages: ReadBool("ARCHIVE", "DOWNSAMPLE_IMAGES", false),
            MaximumImageDpi: ReadInt("ARCHIVE", "MAXIMUM_IMAGE_DPI", 0),
            JpegQuality: ReadInt("ARCHIVE", "JPEG_QUALITY", 100),
            CompressContentStreams: ReadBool("ARCHIVE", "COMPRESS_CONTENT_STREAMS", true),
            BestCompression: ReadBool("ARCHIVE", "BEST_COMPRESSION", false),
            PreserveSourceMetadata: ReadBool("ARCHIVE", "PRESERVE_SOURCE_METADATA", true),
            FontPolicy: ReadString("ARCHIVE", "FONT_POLICY", "system-fonts-with-fallback"),
            EncryptionPolicy: ReadString("ARCHIVE", "ENCRYPTION_POLICY", "preserve-unencrypted-or-reject-password-protected"),
            LargeMarkupPolicy: ReadString("ARCHIVE", "LARGE_MARKUP_POLICY", "visual-layout"),
            StructuralValidation: ReadBool("ARCHIVE", "STRUCTURAL_VALIDATION", true)),
        new PdfOutputProfile(
            "compact",
            PdfVersion: ReadInt("COMPACT", "PDF_VERSION", 17),
            PreserveJpegData: ReadBool("COMPACT", "PRESERVE_JPEG_DATA", false),
            DownsampleImages: ReadBool("COMPACT", "DOWNSAMPLE_IMAGES", true),
            MaximumImageDpi: ReadInt("COMPACT", "MAXIMUM_IMAGE_DPI", 150),
            JpegQuality: ReadInt("COMPACT", "JPEG_QUALITY", 75),
            CompressContentStreams: ReadBool("COMPACT", "COMPRESS_CONTENT_STREAMS", true),
            BestCompression: ReadBool("COMPACT", "BEST_COMPRESSION", true),
            PreserveSourceMetadata: ReadBool("COMPACT", "PRESERVE_SOURCE_METADATA", false),
            FontPolicy: ReadString("COMPACT", "FONT_POLICY", "system-fonts-with-fallback"),
            EncryptionPolicy: ReadString("COMPACT", "ENCRYPTION_POLICY", "none"),
            LargeMarkupPolicy: ReadString("COMPACT", "LARGE_MARKUP_POLICY", "visual-layout"),
            StructuralValidation: ReadBool("COMPACT", "STRUCTURAL_VALIDATION", true)),
        new PdfOutputProfile(
            "compatibility",
            PdfVersion: ReadInt("COMPATIBILITY", "PDF_VERSION", 14),
            PreserveJpegData: ReadBool("COMPATIBILITY", "PRESERVE_JPEG_DATA", true),
            DownsampleImages: ReadBool("COMPATIBILITY", "DOWNSAMPLE_IMAGES", false),
            MaximumImageDpi: ReadInt("COMPATIBILITY", "MAXIMUM_IMAGE_DPI", 0),
            JpegQuality: ReadInt("COMPATIBILITY", "JPEG_QUALITY", 100),
            CompressContentStreams: ReadBool("COMPATIBILITY", "COMPRESS_CONTENT_STREAMS", true),
            BestCompression: ReadBool("COMPATIBILITY", "BEST_COMPRESSION", false),
            PreserveSourceMetadata: ReadBool("COMPATIBILITY", "PRESERVE_SOURCE_METADATA", false),
            FontPolicy: ReadString("COMPATIBILITY", "FONT_POLICY", "system-fonts-with-fallback"),
            EncryptionPolicy: ReadString("COMPATIBILITY", "ENCRYPTION_POLICY", "none"),
            LargeMarkupPolicy: ReadString("COMPATIBILITY", "LARGE_MARKUP_POLICY", "visual-layout"),
            StructuralValidation: ReadBool("COMPATIBILITY", "STRUCTURAL_VALIDATION", true))
    ];

    private static int ReadInt(string profile, string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable($"INACTIVEPDF_PROFILE_{profile}_{name}"), out var value) ? value : fallback;

    private static bool ReadBool(string profile, string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable($"INACTIVEPDF_PROFILE_{profile}_{name}"), out var value) ? value : fallback;

    private static string ReadString(string profile, string name, string fallback) =>
        Environment.GetEnvironmentVariable($"INACTIVEPDF_PROFILE_{profile}_{name}") is { Length: > 0 } value ? value : fallback;

    public static IReadOnlyList<PdfOutputProfile> All => Definitions;

    public static PdfOutputProfile Resolve(string? name)
    {
        var requested = string.IsNullOrWhiteSpace(name) ? "archive" : name.Trim();
        return Definitions.FirstOrDefault(profile => profile.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConversionFormatException("unsupported_profile", $"Unsupported PDF output profile '{requested}'. Supported profiles: {string.Join(", ", Definitions.Select(profile => profile.Name))}.");
    }
}

public sealed class ConversionFormatException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code) ? "invalid_format" : code;
}
