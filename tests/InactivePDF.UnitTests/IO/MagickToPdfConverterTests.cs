using ImageMagick;
using System.Text;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.IO;

namespace InactivePDF.UnitTests.IO;

public sealed class MagickToPdfConverterTests
{
    [Fact]
    public void ConvertCreatesOnePdfPagePerInputImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = CreateImage(root, "first.png", 20, 10, MagickColors.Red);
            var second = CreateImage(root, "second.png", 10, 20, MagickColors.Blue);
            var output = Path.Combine(root, "images.pdf");

            var result = new MagickToPdfConverter().Convert(new[] { first, second }, output);
            var inspection = new PdfSharpOperations().Inspect(output);

            Assert.Equal(2, result.PageCount);
            Assert.Equal(2, inspection.PageCount);
            Assert.True(result.LengthBytes > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveProfilePreservesJpegDataAndSetsPdfProfileMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "photo.jpg");
            using (var image = new MagickImage(MagickColors.Green, 64, 64))
            {
                image.Format = MagickFormat.Jpeg;
                image.Quality = 90;
                image.Write(input);
            }

            var output = Path.Combine(root, "photo.pdf");
            new MagickToPdfConverter().Convert([input], output, PdfOutputProfileCatalog.Resolve("archive"));
            var inspection = new PdfSharpOperations().Inspect(output);
            var bytes = await File.ReadAllBytesAsync(output);

            Assert.Equal(17, inspection.PdfVersion);
            Assert.Equal("InactivePDF", inspection.Creator);
            Assert.Contains("/DCTDecode", Encoding.ASCII.GetString(bytes));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ArchiveProfileConvertsMultipageTiffWithoutDroppingFrames()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "pages.tiff");
            using (var pages = new MagickImageCollection())
            {
                using var first = new MagickImage(MagickColors.Red, 32, 32);
                using var second = new MagickImage(MagickColors.Blue, 32, 32);
                first.Format = MagickFormat.Tiff;
                second.Format = MagickFormat.Tiff;
                pages.Add(first.Clone());
                pages.Add(second.Clone());
                pages.Write(input);
            }

            var output = Path.Combine(root, "pages.pdf");
            new MagickToPdfConverter().Convert([input], output, PdfOutputProfileCatalog.Resolve("archive"));

            Assert.Equal(2, new PdfSharpOperations().Inspect(output).PageCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateImage(string root, string name, int width, int height, MagickColor color)
    {
        var path = Path.Combine(root, name);
        using var image = new MagickImage(color, (uint)width, (uint)height);
        image.Format = MagickFormat.Png;
        image.Write(path);
        return path;
    }
}
