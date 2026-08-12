namespace InactivePDF.Domain.Models;

public sealed record ConversionError(
    string Code,
    string Message,
    bool IsRetryable);
