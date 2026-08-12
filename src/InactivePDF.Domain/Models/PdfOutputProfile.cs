namespace InactivePDF.Domain.Models;

/// <summary>
/// A named, observable PDF policy. The profile is applied by every conversion route,
/// including images, generated text, PDF merge, and PDF pass-through.
/// </summary>
public sealed record PdfOutputProfile(
    string Name,
    int PdfVersion,
    bool PreserveJpegData,
    bool DownsampleImages,
    int MaximumImageDpi,
    int JpegQuality,
    bool CompressContentStreams,
    bool BestCompression,
    bool PreserveSourceMetadata,
    string FontPolicy,
    string EncryptionPolicy,
    string LargeMarkupPolicy,
    bool StructuralValidation)
{
}
