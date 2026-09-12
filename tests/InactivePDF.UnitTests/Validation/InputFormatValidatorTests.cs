using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Validation;

namespace InactivePDF.UnitTests.Validation;

public sealed class InputFormatValidatorTests
{
    [Fact]
    public async Task ValidPngAndDeclaredMimeTypeAreAccepted()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "image.png");
            await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var result = InputFormatValidator.Validate(path, "image.png", "image/png");

            Assert.Equal(".png", result.Format.Extension);
            Assert.Equal("png", result.Signature);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task SignatureMismatchIsRejectedWithStructuredCode()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "image.png");
            await File.WriteAllTextAsync(path, "not a png");

            var exception = Assert.Throws<ConversionFormatException>(() => InputFormatValidator.Validate(path, "image.png", "image/png"));

            Assert.Equal("signature_missing", exception.Code);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ZipOfficePackageAndRtfHeaderAreValidated()
    {
        var root = CreateRoot();
        try
        {
            var package = Path.Combine(root, "document.docx");
            await File.WriteAllBytesAsync(package, [0x50, 0x4B, 0x03, 0x04, 0x00]);
            Assert.Throws<ConversionFormatException>(() => InputFormatValidator.Validate(package, "document.docx"));

            var rtf = Path.Combine(root, "document.rtf");
            await File.WriteAllTextAsync(rtf, "{\\rtf1\\ansi text}");
            Assert.Equal(ConversionFormatCategory.Rtf, InputFormatValidator.Validate(rtf, "document.rtf", "text/rtf").Format.Category);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task OpenDocumentSpreadsheetPackageIsAccepted()
    {
        var root = CreateRoot();
        try
        {
            var package = Path.Combine(root, "spreadsheet.ods");
            await File.WriteAllBytesAsync(package, [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00]);

            var exception = Assert.Throws<ConversionFormatException>(() => InputFormatValidator.Validate(package, "spreadsheet.ods"));

            Assert.Equal("invalid_package", exception.Code);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ProductionTextValidationStopsAfterTheBoundedDiagnosticSample()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "large.txt");
            var content = Enumerable.Repeat((byte)'a', 2_000_000).ToArray();
            content[1_500_000] = 0xC3;
            content[1_500_001] = 0x28;
            await File.WriteAllBytesAsync(path, content);

            Assert.Throws<ConversionFormatException>(() => InputFormatValidator.Validate(path, "large.txt", executionMode: ConversionExecutionMode.Development));
            Assert.Equal("text", InputFormatValidator.Validate(path, "large.txt", executionMode: ConversionExecutionMode.Production).Signature);
        }
        finally { DeleteRoot(root); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
