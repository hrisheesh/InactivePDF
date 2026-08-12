namespace InactivePDF.Domain.Models;

public sealed record OfficeConversionResult(string OutputPath, long LengthBytes, TimeSpan Duration, int ExitCode);
