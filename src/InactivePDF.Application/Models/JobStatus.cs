using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Models;

public sealed record JobStatus(
    Guid JobId,
    string CorrelationId,
    ConversionOperation Operation,
    ConversionJobState State,
    DateTimeOffset AcceptedAt,
    DateTimeOffset UpdatedAt,
    int Attempts,
    string? OutputPath = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
