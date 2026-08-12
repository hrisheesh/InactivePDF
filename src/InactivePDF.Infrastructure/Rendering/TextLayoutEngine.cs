using PdfSharp.Drawing;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Rendering;

public sealed class TextLayoutEngine
{
    public static TextLineLayout Layout(string text, TextPdfOptions options, XFont font)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(font);

        var maxWidth = options.PageWidthPoints - (2 * options.MarginPoints);
        var lineHeight = font.GetHeight() * options.LineSpacing;
        if (maxWidth <= 0 || lineHeight <= 0) throw new ArgumentException("Margins and page dimensions leave no drawable area.", nameof(options));

        using var graphics = XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards, null);
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            WrapParagraph(paragraph, maxWidth, font, graphics, lines);
        }

        if (lines.Count == 0) lines.Add(string.Empty);
        return new TextLineLayout(lines, lineHeight);
    }

    private static void WrapParagraph(string paragraph, double maxWidth, XFont font, XGraphics graphics, List<string> lines)
    {
        if (paragraph.Length == 0)
        {
            lines.Add(string.Empty);
            return;
        }

        var start = 0;
        while (start < paragraph.Length)
        {
            var end = FindFittingEnd(paragraph, start, maxWidth, font, graphics);
            var breakAt = end;
            if (end < paragraph.Length)
            {
                var whitespace = paragraph.LastIndexOf(' ', end - 1, end - start);
                if (whitespace >= start) breakAt = whitespace;
            }

            var line = paragraph[start..breakAt].TrimEnd();
            lines.Add(line);
            start = breakAt;
            while (start < paragraph.Length && paragraph[start] == ' ') start++;
        }
    }

    private static int FindFittingEnd(string text, int start, double maxWidth, XFont font, XGraphics graphics)
    {
        var low = start + 1;
        var high = text.Length;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Measure(text[start..middle], font, graphics) <= maxWidth) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    private static double Measure(string text, XFont font, XGraphics graphics) => graphics.MeasureString(text, font).Width;
}
