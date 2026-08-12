namespace InactivePDF.Domain.Contracts;

public interface IConversionJobIdGenerator
{
    Guid Create();
}
