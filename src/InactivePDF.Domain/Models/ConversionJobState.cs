namespace InactivePDF.Domain.Models;

public enum ConversionJobState
{
    Accepted,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Retrying,
    DeadLettered
}
