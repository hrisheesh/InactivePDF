namespace InactivePDF.Domain.Models;

public sealed record PdfInspection(
    string FilePath,
    long LengthBytes,
    int PageCount,
    bool IsEncrypted,
    int PdfVersion = 0,
    string? Creator = null);
