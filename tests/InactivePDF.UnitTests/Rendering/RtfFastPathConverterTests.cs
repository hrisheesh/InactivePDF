using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.Rendering;

namespace InactivePDF.UnitTests.Rendering;

public sealed class RtfFastPathConverterTests
{
    [Fact]
    public async Task ConvertsSimpleRtfWithoutLibreOffice()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.rtf");
        var output = Path.Combine(root, "output.pdf");
        await File.WriteAllTextAsync(input, "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}\\f0\\fs24 First line\\par Second line.}");

        try
        {
            var converter = new RtfFastPathConverter(new PdfTextGenerator());
            Assert.True(converter.TryConvert(input, output, PdfOutputProfileCatalog.Resolve("archive"), CancellationToken.None));
            Assert.True(File.Exists(output));
            Assert.True(new FileInfo(output).Length > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FallsBackForFormattedRtfOutsideTheFastSubset()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.rtf");
        var output = Path.Combine(root, "output.pdf");
        await File.WriteAllTextAsync(input, "{\\rtf1\\ansi{\\pict\\pngblip unsupported-image-data}}");

        try
        {
            var converter = new RtfFastPathConverter(new PdfTextGenerator());
            Assert.False(converter.TryConvert(input, output, PdfOutputProfileCatalog.Resolve("archive"), CancellationToken.None));
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
