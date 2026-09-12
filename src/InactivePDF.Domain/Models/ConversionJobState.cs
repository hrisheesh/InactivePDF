namespace InactivePDF.Domain.Models;

public enum ConversionJobState
{
    Accepted,
    Processing,
    Running = Processing,
    Succeeded,
    Failed,
    Cancelled,
    Retrying,
    DeadLettered,
    Queued,
    WaitingForResources,
    Interrupted
}
