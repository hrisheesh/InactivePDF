using InactivePDF.Application.Models;

namespace InactivePDF.Application.Abstractions;

public interface IConversionWorkProcessor
{
    Task<string> ProcessAsync(ConversionWorkItem item, CancellationToken cancellationToken = default);
}
