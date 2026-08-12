using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface ITextPdfGenerator
{
    TextPdfResult Create(string text, string outputPath, TextPdfOptions? options = null);
}
