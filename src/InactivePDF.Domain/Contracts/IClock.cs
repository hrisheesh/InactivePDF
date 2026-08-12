namespace InactivePDF.Domain.Contracts;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
