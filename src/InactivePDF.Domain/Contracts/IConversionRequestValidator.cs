using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface IConversionRequestValidator
{
    IReadOnlyList<ConversionError> Validate(ConversionRequest request);
}
