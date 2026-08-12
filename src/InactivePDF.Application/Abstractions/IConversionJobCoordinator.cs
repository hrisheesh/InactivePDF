using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Abstractions;

public interface IConversionJobCoordinator
{
    ConversionJob Accept(ConversionRequest request);
}
