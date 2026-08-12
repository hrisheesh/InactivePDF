using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.IO;

namespace InactivePDF.Infrastructure.Rendering;

public sealed class PdfTextGenerator : ITextPdfGenerator
{
    private const int StreamingTextThreshold = 1_000_000;

    public TextPdfResult Create(string text, string outputPath, TextPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new TextPdfOptions();
        // The raw streaming writer uses a compact WinAnsi font. Keep that fast path for
        // documents it can represent, but route Unicode text through PDFsharp's embedded
        // font path so large Hindi, CJK, emoji, and other non-WinAnsi input is never
        // silently replaced with question marks.
        if (text.Length >= StreamingTextThreshold && StreamingTextPdfGenerator.SupportsWinAnsi(text))
        {
            return StreamingTextPdfGenerator.Create(text, outputPath, options);
        }

        ValidateOptions(options);
        var profile = options.OutputProfile ?? PdfOutputProfileCatalog.Resolve("archive");
        var output = ValidateOutput(outputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.tmp.pdf");
        GlobalFontSettings.FallbackFontResolver ??= new SystemFontResolver();
        var font = new XFont(options.FontFamily, options.FontSizePoints, XFontStyleEx.Regular);
        var layout = TextLayoutEngine.Layout(text, options, font);
        var linesPerPage = Math.Max(1, (int)Math.Floor((options.PageHeightPoints - (2 * options.MarginPoints)) / layout.LineHeight));
        var pageCount = (layout.Lines.Count + linesPerPage - 1) / linesPerPage;

        using var document = new PdfDocument();
        try
        {
            PdfProfileDocumentConfigurator.Apply(document, profile);
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(options.PageWidthPoints);
                page.Height = XUnit.FromPoint(options.PageHeightPoints);
                using var graphics = XGraphics.FromPdfPage(page);
                var start = pageIndex * linesPerPage;
                var count = Math.Min(linesPerPage, layout.Lines.Count - start);
                for (var lineIndex = 0; lineIndex < count; lineIndex++)
                {
                    var y = options.MarginPoints + ((lineIndex + 1) * layout.LineHeight);
                    graphics.DrawString(layout.Lines[start + lineIndex], font, XBrushes.Black, options.MarginPoints, y);
                }
            }

            document.Save(temporary);
            File.Move(temporary, output, overwrite: true);
            return new TextPdfResult(output, pageCount, new FileInfo(output).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateOptions(TextPdfOptions options)
    {
        if (options.PageWidthPoints <= 0 || options.PageHeightPoints <= 0 || options.MarginPoints < 0 || options.FontSizePoints <= 0 || options.LineSpacing <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Page, margin, font, and line spacing values must be positive.");
        if ((2 * options.MarginPoints) >= Math.Min(options.PageWidthPoints, options.PageHeightPoints))
            throw new ArgumentException("Margins must leave drawable page area.", nameof(options));
    }

    private static string ValidateOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output path must have a .pdf extension.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }
}
