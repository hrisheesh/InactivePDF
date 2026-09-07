using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Watermarks;

public static class WatermarkProfileValidator
{
    public static IReadOnlyList<string> Validate(WatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.Kind == WatermarkKind.Text && string.IsNullOrWhiteSpace(options.Text)) errors.Add("Text is required for a text watermark.");
        if (options.Kind == WatermarkKind.Image && string.IsNullOrWhiteSpace(options.ImagePath)) errors.Add("ImagePath is required for an image watermark.");
        if (options.Kind == WatermarkKind.Image && !string.IsNullOrWhiteSpace(options.ImagePath) && Path.GetFileName(options.ImagePath) != options.ImagePath)
            errors.Add("ImagePath must be a single asset filename; paths outside the configured asset directory are not allowed.");
        if (options.FontSize is <= 0 or > 500) errors.Add("FontSize must be between 0 and 500 points.");
        if (options.Opacity is < 0 or > 1) errors.Add("Opacity must be between 0 and 1.");
        if (options.Rotation is < -360 or > 360) errors.Add("Rotation must be between -360 and 360 degrees.");
        if (string.IsNullOrWhiteSpace(options.Pages)) errors.Add("Pages is required.");
        else if (!PageRanges.TryParse(options.Pages, out _)) errors.Add("Pages must be all, first, last, odd, even, or a valid range such as 1,3-5.");
        if (options.Width is <= 0 or > 2000) errors.Add("Width must be between 0 and 2000 points when specified.");
        if (options.Height is <= 0 or > 2000) errors.Add("Height must be between 0 and 2000 points when specified.");
        if (string.IsNullOrWhiteSpace(options.Color) || !options.Color.StartsWith('#')) errors.Add("Color must be a hexadecimal value such as #808080.");
        return errors;
    }

    public static bool TryParsePages(string pages, out IReadOnlySet<int> selected) => PageRanges.TryParse(pages, out selected);

    private static class PageRanges
    {
        public static bool TryParse(string value, out IReadOnlySet<int> pages)
        {
            var result = new HashSet<int>();
            var normalized = value.Trim().ToLowerInvariant();
            if (normalized is "all" or "first" or "last" or "odd" or "even") { pages = result; return true; }
            foreach (var part in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split('-', StringSplitOptions.TrimEntries);
                if (pieces.Length is < 1 or > 2 || !int.TryParse(pieces[0], out var start) || start < 1) { pages = result; return false; }
                var end = pieces.Length == 2 && int.TryParse(pieces[1], out var parsedEnd) ? parsedEnd : start;
                if (end < start || end < 1 || end - start > 10000) { pages = result; return false; }
                for (var page = start; page <= end; page++) result.Add(page);
            }
            pages = result;
            return result.Count > 0;
        }
    }
}
