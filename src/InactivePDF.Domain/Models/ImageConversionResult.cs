namespace InactivePDF.Domain.Models;

public sealed record ImageConversionResult(string OutputPath, int PageCount, long LengthBytes);
