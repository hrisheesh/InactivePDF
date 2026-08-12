using InactivePDF.Domain.Contracts;

namespace InactivePDF.Infrastructure.Configuration;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
