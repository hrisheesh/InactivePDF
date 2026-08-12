using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Rendering;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests.Rendering;

public sealed class PdfTextGeneratorTests
{
    [Fact]
    public void CreateProducesValidPdfForLongWrappedText()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "text.pdf");
            var text = string.Join(' ', Enumerable.Repeat("A deterministic text conversion engine must wrap content safely.", 300));
            var result = new PdfTextGenerator().Create(text, output);

            Assert.True(result.PageCount > 1);
            Assert.True(result.LengthBytes > 0);
            Assert.Equal(result.PageCount, new PdfSharpOperations().Inspect(output).PageCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateRejectsInvalidPageOptions()
    {
        var generator = new PdfTextGenerator();

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Create("text", "output.pdf", new TextPdfOptions(PageWidthPoints: 0)));
    }

    [Fact]
    public void CreateUsesBoundedMemoryPathForLargeText()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "large-text.pdf");
            var text = string.Concat(Enumerable.Repeat("Large text conversion must remain bounded and valid. ", 25_000));

            var result = new PdfTextGenerator().Create(text, output);
            var inspection = new PdfSharpOperations().Inspect(output);

            Assert.True(result.PageCount > 1);
            Assert.Equal(result.PageCount, inspection.PageCount);
            Assert.True(result.LengthBytes > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreatePreservesUnicodeCharactersInsteadOfUsingTheAsciiStreamingWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "unicode.pdf");
            var text = "हिन्दी 日本語 العربية Ελληνικά 😀";
            var result = new PdfTextGenerator().Create(text, output);

            Assert.True(result.LengthBytes > 0);
            Assert.Equal(1, new PdfSharpOperations().Inspect(output).PageCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
