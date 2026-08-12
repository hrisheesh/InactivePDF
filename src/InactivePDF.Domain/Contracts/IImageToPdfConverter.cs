using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface IImageToPdfConverter
{
    ImageConversionResult Convert(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null);
}
