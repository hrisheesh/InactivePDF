using System.Diagnostics;
using ImageMagick;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InactivePDF.UnitTests.Watermarks;

public sealed class PdfWatermarkRenderingTests
{
    private static readonly string[] Renderers = ["/opt/homebrew/bin/pdftoppm", "/usr/local/bin/pdftoppm", "/usr/bin/pdftoppm"];
    [Fact]
    public void ImageWatermarkUsesProportionalSizingAndOpacity()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.pdf");
        var output = Path.Combine(root, "output.pdf");
        var asset = Path.Combine(root, "asset.png");
        using (var image = new MagickImage(MagickColors.Red, 80, 40)) image.Write(asset, MagickFormat.Png);
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
            document.Save(input);
        }
        try
        {
            new PdfWatermarkService().Apply(input, output, new WatermarkOptions(Kind: WatermarkKind.Image, ImagePath: asset, Width: 160, Opacity: .25, Rotation: 12));
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.Single(result.Pages);
            Assert.True(new FileInfo(output).Length > new FileInfo(input).Length);
            Assert.Contains("/SMask", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output)), StringComparison.Ordinal);
            var renderer = Renderers.FirstOrDefault(File.Exists);
            if (renderer is null) return;
            var prefix = Path.Combine(root, "rendered");
            using var process = Process.Start(new ProcessStartInfo(renderer, $"-f 1 -singlefile -png \"{output}\" \"{prefix}\"") { RedirectStandardError = true, UseShellExecute = false });
            process!.WaitForExit(15000);
            Assert.Equal(0, process.ExitCode);
            using var rendered = new MagickImage(prefix + ".png");
            Assert.True(rendered.Width > 0 && rendered.Height > 0);
        }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } }
    }
}
