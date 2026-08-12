using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface IOfficeDocumentConverter
{
    Task<OfficeConversionResult> ConvertAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default);
}
