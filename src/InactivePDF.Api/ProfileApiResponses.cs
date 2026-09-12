using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Models;

namespace InactivePDF.Api;

internal static class ProfileApiResponses
{
    public static object Catalog(WatermarkProfileStore store) => new
    {
        pdfOutput = PdfOutputProfileCatalog.All.ToArray(),
        watermark = store.List()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new { name = pair.Key, options = pair.Value })
            .ToArray()
    };

    public static object? Lookup(string name, WatermarkProfileStore store)
    {
        var output = PdfOutputProfileCatalog.All.FirstOrDefault(profile =>
            profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var hasWatermark = store.TryGet(name, out var watermark);
        return output is null && !hasWatermark
            ? null
            : new { name, pdfOutput = output, watermark = hasWatermark ? new { name, options = watermark } : null };
    }
}
