namespace InactivePDF.Domain.Models;

public sealed record TextPdfOptions(
    double PageWidthPoints = 612,
    double PageHeightPoints = 792,
    double MarginPoints = 54,
    string FontFamily = "Arial",
    double FontSizePoints = 10,
    double LineSpacing = 1.2,
    PdfOutputProfile? OutputProfile = null);
