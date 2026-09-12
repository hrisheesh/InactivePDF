using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;

namespace InactivePDF.Api;

internal static class JobApiResponses
{
    public static object ToPublic(JobStatus status) => new
    {
        status.JobId,
        status.CorrelationId,
        status.Operation,
        status.State,
        createdAt = status.AcceptedAt,
        status.AcceptedAt,
        status.UpdatedAt,
        status.Attempts,
        retryCount = status.RetryCount,
        status.Source,
        status.Format,
        status.Profile,
        status.WatermarkProfile,
        progressPercent = Math.Clamp(status.ProgressPercent, 0, 100),
        status.CurrentWorker,
        status.CurrentLane,
        queueWaitMilliseconds = status.QueueWaitMilliseconds,
        processingMilliseconds = status.ProcessingMilliseconds,
        status.InputBytes,
        status.OutputBytes,
        status.CompressionPercent,
        status.ErrorCode,
        status.ErrorMessage,
        outputAvailable = status.State == ConversionJobState.Succeeded,
        outputUrl = status.State == ConversionJobState.Succeeded ? $"/v1/jobs/{status.JobId:D}/output" : null
    };
}
