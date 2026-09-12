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
    string? ErrorMessage = null,
    Guid? OwnerApiKeyId = null,
    string Source = "API",
    string? Format = null,
    int ProgressPercent = 0,
    string? CurrentWorker = null,
    string? CurrentLane = null,
    long QueueWaitMilliseconds = 0,
    long ProcessingMilliseconds = 0,
    long InputBytes = 0,
    long OutputBytes = 0,
    double? CompressionPercent = null,
    DateTimeOffset? IdempotencyExpiresAt = null,
    int RetryCount = 0,
    string Profile = "archive",
    string? WatermarkProfile = null);
