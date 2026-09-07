namespace InactivePDF.Domain.Models;

public sealed record ConversionOptions(
    string Profile = "archive",
    TimeSpan? Timeout = null,
    bool PreserveExistingPdf = true,
    string? WatermarkProfile = null,
    WatermarkOptions? Watermark = null);
