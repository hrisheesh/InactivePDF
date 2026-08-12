namespace InactivePDF.Application.Models;

/// <summary>
/// Safe, durable diagnostics for one conversion attempt. The complete exception text is
/// retained for internal diagnostics; callers should expose it only on trusted endpoints.
/// </summary>
public sealed record ConversionAttemptDiagnostic(
    int Attempt,
    DateTimeOffset RecordedAt,
    string ErrorCode,
    string ErrorType,
    string Message,
    string Exception,
    bool Retryable);
