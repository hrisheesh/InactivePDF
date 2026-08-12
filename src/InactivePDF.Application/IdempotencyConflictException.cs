namespace InactivePDF.Application;

public sealed class IdempotencyConflictException(string correlationId)
    : Exception($"The idempotency key '{correlationId}' was already used with a different request.")
{
    public string CorrelationId { get; } = correlationId;
}
