using InactivePDF.Domain.Models;
using PdfSharp.Pdf;

namespace InactivePDF.Infrastructure.IO;

internal static class PdfProfileDocumentConfigurator
{
    public static void Apply(PdfDocument document, PdfOutputProfile profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(profile);
        document.Version = profile.PdfVersion;
        document.Options.CompressContentStreams = profile.CompressContentStreams;
        document.Options.FlateEncodeMode = profile.BestCompression ? PdfFlateEncodeMode.BestCompression : PdfFlateEncodeMode.BestSpeed;
        if (!profile.PreserveSourceMetadata || string.IsNullOrWhiteSpace(document.Info.Creator))
            document.Info.Creator = "InactivePDF";
        if (!profile.PreserveSourceMetadata || string.IsNullOrWhiteSpace(document.Info.Subject))
            document.Info.Subject = $"InactivePDF {profile.Name} profile";
    }
}
