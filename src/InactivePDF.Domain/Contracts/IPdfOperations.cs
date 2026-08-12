using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface IPdfOperations
{
    PdfInspection Inspect(string filePath);

    void Merge(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null);

    void ApplyProfile(string filePath, PdfOutputProfile profile);
}
