namespace InactivePDF.Domain.Models;

public sealed class ConversionWorkerExecutionException(string message, bool isRetryable = true) : Exception(message)
{
    public bool IsRetryable { get; } = isRetryable;
}

public sealed class ConversionWorkerTimeoutException(string message) : TimeoutException(message);

public sealed class ConversionResourceLimitException(string message) : IOException(message);

public sealed class ConversionLeaseLostException(Guid jobId)
    : Exception($"The durable lease for conversion job '{jobId:N}' was lost.");
