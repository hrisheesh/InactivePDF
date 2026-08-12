using PdfSharp.Pdf;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.IO;

namespace InactivePDF.UnitTests.IO;

public sealed class PdfSharpOperationsTests
{
    [Fact]
    public void MergePreservesInputOrderAndPageCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = CreatePdf(root, "first.pdf", pageCount: 2);
            var second = CreatePdf(root, "second.pdf", pageCount: 3);
            var output = Path.Combine(root, "merged.pdf");
            var operations = new PdfSharpOperations();

            operations.Merge(new[] { first, second }, output);
            var inspection = operations.Inspect(output);

            Assert.Equal(5, inspection.PageCount);
            Assert.Equal(new FileInfo(output).Length, inspection.LengthBytes);
            Assert.False(inspection.IsEncrypted);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeRejectsAnEmptyInputSet()
    {
        var operations = new PdfSharpOperations();

        Assert.Throws<ArgumentException>(() => operations.Merge(Array.Empty<string>(), "output.pdf"));
    }

    [Fact]
    public void MergeAppliesProfileVersionAndMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = CreatePdf(root, "first.pdf", pageCount: 1);
            var output = Path.Combine(root, "compatibility.pdf");

            new PdfSharpOperations().Merge([first], output, PdfOutputProfileCatalog.Resolve("compatibility"));
            var inspection = new PdfSharpOperations().Inspect(output);

            Assert.Equal(14, inspection.PdfVersion);
            Assert.Equal("InactivePDF", inspection.Creator);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectRejectsMalformedPdfWithStructuredReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "broken.pdf");
            File.WriteAllText(path, "%PDF-1.7\nnot a complete PDF");

            var exception = Assert.Throws<ConversionFormatException>(() => new PdfSharpOperations().Inspect(path));

            Assert.Equal("invalid_pdf", exception.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectRejectsPasswordProtectedPdfWithStructuredReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "protected.pdf");
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.SecuritySettings.UserPassword = "secret";
                document.Save(path);
            }

            var exception = Assert.Throws<ConversionFormatException>(() => new PdfSharpOperations().Inspect(path));

            Assert.Equal("encrypted_input", exception.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePdf(string root, string fileName, int pageCount)
    {
        var path = Path.Combine(root, fileName);
        using var document = new PdfDocument();
        for (var index = 0; index < pageCount; index++)
        {
            document.AddPage();
        }

        document.Save(path);
        return path;
    }
}
