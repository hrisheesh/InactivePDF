namespace InactivePDF.Domain.Models;

public sealed record TextPdfResult(string OutputPath, int PageCount, long LengthBytes);
