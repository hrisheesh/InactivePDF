using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Models;

/// <summary>
/// Durable terminal failure plus the original file-backed work item so an operator can
/// deliberately replay it with a new correlation identity.
/// </summary>
public sealed record DeadLetterRecord(
    Guid JobId,
    string CorrelationId,
    ConversionOperation Operation,
    int Attempt,
    string ErrorCode,
    string ErrorType,
    string Exception,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ConversionAttemptDiagnostic> AttemptHistory,
    ConversionWorkItem WorkItem);
