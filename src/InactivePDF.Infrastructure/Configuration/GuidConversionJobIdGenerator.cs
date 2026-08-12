using InactivePDF.Domain.Contracts;

namespace InactivePDF.Infrastructure.Configuration;

public sealed class GuidConversionJobIdGenerator : IConversionJobIdGenerator
{
    public Guid Create() => Guid.NewGuid();
}
