namespace InactivePDF.Domain.Models;

public sealed record ConversionJob(
    Guid Id,
    string CorrelationId,
    ConversionOperation Operation,
    ConversionJobState State,
    DateTimeOffset AcceptedAt,
    Guid? OwnerApiKeyId = null);
