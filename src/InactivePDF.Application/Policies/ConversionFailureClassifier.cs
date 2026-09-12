using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Application.Policies;

public sealed record ConversionFailureClassification(string Code, string Message, bool IsRetryable);

public static class ConversionFailureClassifier
{
    public static ConversionFailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = SafeMessage(exception);

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

    public static string SafeMessageForCode(string? code) => code switch
    {
        "cancelled" => "The conversion was cancelled.",
        "encrypted_input" => "The PDF is password protected and cannot be converted.",
        "invalid_pdf" => "The PDF is invalid or structurally unsupported.",
        "invalid_package" or "package_type_mismatch" => "The document package is invalid or does not match its extension.",
        "invalid_text_encoding" => "The text input uses an unsupported encoding.",
        "signature_mismatch" => "The file content does not match its declared format.",
        "unsupported_extension" => "The input format is not supported.",
        "unsupported_profile" => "The requested PDF output profile is not supported.",
        "invalid_watermark" or "invalid_watermark_profile" => "The watermark options are invalid.",
        "watermark_profile_not_found" => "The requested watermark profile does not exist.",
        "resource_limit_exceeded" => "The conversion exceeded a configured resource limit.",
        "worker_timeout" or "conversion_timeout" => "The conversion exceeded the configured time limit.",
        "worker_execution_failed" => "The isolated conversion worker failed.",
        "input_missing" => "The conversion input or workspace is no longer available.",
        "access_denied" => "The conversion was denied by workspace security policy.",
        "invalid_input" => "The request or input is invalid.",
        "io_error" => "The conversion could not complete because of a protected storage or I/O error.",
        "conversion_lease_lost" => "The conversion lease was lost before completion.",
        _ => "The conversion could not be completed."
    };

    private static string SafeMessage(Exception exception) => exception switch
    {
        OperationCanceledException => "The conversion was cancelled.",
        ConversionFormatException format => SafeMessageForCode(format.Code),
        ConversionResourceLimitException => SafeMessageForCode("resource_limit_exceeded"),
        ConversionWorkerTimeoutException => SafeMessageForCode("worker_timeout"),
        ConversionWorkerExecutionException => SafeMessageForCode("worker_execution_failed"),
        ConversionLeaseLostException => SafeMessageForCode("conversion_lease_lost"),
        TimeoutException => SafeMessageForCode("conversion_timeout"),
        FileNotFoundException or DirectoryNotFoundException => SafeMessageForCode("input_missing"),
        UnauthorizedAccessException => SafeMessageForCode("access_denied"),
        NotSupportedException or InvalidDataException or ArgumentException => SafeMessageForCode("invalid_input"),
        IOException => SafeMessageForCode("io_error"),
        _ => SafeMessageForCode(null)
    };
}
