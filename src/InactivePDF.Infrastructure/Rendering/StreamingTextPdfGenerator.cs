using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Infrastructure.Rendering;

internal sealed class StreamingTextPdfGenerator
{
    public static bool SupportsWinAnsi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.All(character => character is >= '\u0000' and <= '\u00FF' or
            '\u0152' or '\u0153' or '\u0160' or '\u0161' or '\u0178' or '\u017D' or '\u017E' or
            '\u0192' or '\u02C6' or '\u02DC' or '\u2013' or '\u2014' or '\u2018' or '\u2019' or
            '\u201A' or '\u201C' or '\u201D' or '\u201E' or '\u2020' or '\u2021' or '\u2026' or
            '\u2030' or '\u2039' or '\u203A' or '\u20AC' or '\u2122');
    }

    public static TextPdfResult Create(string text, string outputPath, TextPdfOptions options)
    {
        ValidateOptions(options);
        var profile = options.OutputProfile ?? PdfOutputProfileCatalog.Resolve("archive");
        var output = ValidateOutput(outputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.tmp.pdf");
        var lineHeight = options.FontSizePoints * 1.2d * options.LineSpacing;
        var linesPerPage = Math.Max(1, (int)Math.Floor((options.PageHeightPoints - (2 * options.MarginPoints)) / lineHeight));
        var maxCharactersPerLine = Math.Max(1, (int)Math.Floor((options.PageWidthPoints - (2 * options.MarginPoints)) / (options.FontSizePoints * 0.5d)));
        var pageLines = new List<string>(linesPerPage);
        var pageCount = 0;

        try
        {
            using (var document = new StreamingPdfDocument(temporary, profile))
            {
                foreach (var paragraph in EnumerateParagraphs(text))
                {
                    foreach (var line in Wrap(paragraph, maxCharactersPerLine))
                    {
                        pageLines.Add(line);
                        if (pageLines.Count == linesPerPage)
                        {
                            document.AddTextPage(pageLines, options.PageWidthPoints, options.PageHeightPoints, options.MarginPoints, options.FontSizePoints, lineHeight);
                            pageLines.Clear();
                            pageCount++;
                        }
                    }
                }

                if (pageLines.Count > 0 || pageCount == 0)
                {
                    document.AddTextPage(pageLines, options.PageWidthPoints, options.PageHeightPoints, options.MarginPoints, options.FontSizePoints, lineHeight);
                    pageCount++;
                }
            }

            File.Move(temporary, output, overwrite: true);
            return new TextPdfResult(output, pageCount, new FileInfo(output).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IEnumerable<string> EnumerateParagraphs(string text)
    {
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index != text.Length && text[index] != '\n') continue;
            var length = index - start;
            if (length > 0 && text[start + length - 1] == '\r') length--;
            yield return text.Substring(start, length);
            start = index + 1;
        }
    }

    private static IEnumerable<string> Wrap(string paragraph, int maxCharacters)
    {
        if (paragraph.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var start = 0;
        while (start < paragraph.Length)
        {
            var remaining = paragraph.Length - start;
            var length = Math.Min(maxCharacters, remaining);
            if (length < remaining)
            {
                var whitespace = paragraph.LastIndexOf(' ', start + length - 1, length);
                if (whitespace >= start) length = whitespace - start;
            }

            if (length == 0) length = Math.Min(maxCharacters, remaining);
            yield return paragraph.Substring(start, length).TrimEnd();
            start += length;
            while (start < paragraph.Length && paragraph[start] == ' ') start++;
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
