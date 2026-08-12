using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Application.Policies;

public sealed record ConversionFailureClassification(string Code, string Message, bool IsRetryable);

public static class ConversionFailureClassifier
{
    public static ConversionFailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = Normalize(exception.Message);

        return exception switch
        {
            OperationCanceledException => new("cancelled", message, false),
            ConversionFormatException format => new(format.Code, message, false),
            ConversionResourceLimitException => new("resource_limit_exceeded", message, false),
            ConversionWorkerTimeoutException => new("worker_timeout", message, true),
            ConversionWorkerExecutionException worker => new("worker_execution_failed", message, worker.IsRetryable),
            TimeoutException => new("conversion_timeout", message, true),
            FileNotFoundException or DirectoryNotFoundException => new("input_missing", message, false),
            UnauthorizedAccessException => new("access_denied", message, false),
            NotSupportedException or InvalidDataException or ArgumentException => new("invalid_input", message, false),
            IOException => new("io_error", message, true),
            _ => new("conversion_failed", message, false)
        };
    }

    private static string Normalize(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? "The conversion failed without a diagnostic message."
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
