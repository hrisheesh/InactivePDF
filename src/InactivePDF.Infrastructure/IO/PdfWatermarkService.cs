using InactivePDF.Application.Watermarks;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using ImageMagick;

namespace InactivePDF.Infrastructure.IO;

public sealed class PdfWatermarkService : IWatermarkService
{
    public void Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var errors = WatermarkProfileValidator.Validate(options);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        using var document = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
        for (var index = 0; index < document.PageCount; index++)
        {
            if (!Includes(options.Pages, index + 1, document.PageCount)) continue;
            using var graphics = XGraphics.FromPdfPage(document.Pages[index], options.Layer == WatermarkLayer.Behind ? XGraphicsPdfPageOptions.Prepend : XGraphicsPdfPageOptions.Append);
            var font = new XFont(options.FontFamily, options.FontSize, XFontStyleEx.Regular);
            var brush = new XSolidBrush(ParseColor(options.Color, options.Opacity));
            if (options.Kind == WatermarkKind.Image && !string.IsNullOrWhiteSpace(options.ImagePath))
            {
                var preparedPath = PrepareImage(options.ImagePath, options.Opacity);
                try
                {
                    using var image = XImage.FromFile(preparedPath);
                    var width = options.Width ?? image.PointWidth;
                    var height = options.Height ?? image.PointHeight;
                    if (options.Width.HasValue && !options.Height.HasValue) height = width * image.PointHeight / image.PointWidth;
                    if (options.Height.HasValue && !options.Width.HasValue) width = height * image.PointWidth / image.PointHeight;
                    var point = Position(options.Position, document.Pages[index].Width.Point, document.Pages[index].Height.Point, width, height, options.OffsetX, options.OffsetY);
                    DrawImage(graphics, image, point, width, height, options.Rotation, options.Tile, document.Pages[index].Width.Point, document.Pages[index].Height.Point);
                }
                finally { try { File.Delete(preparedPath); } catch (IOException) { } }
            }
            else
            {
                var text = options.Text;
                var size = graphics.MeasureString(text, font);
                var point = Position(options.Position, document.Pages[index].Width.Point, document.Pages[index].Height.Point, size.Width, size.Height, options.OffsetX, options.OffsetY);
                DrawText(graphics, text, font, brush, point, size, options.Rotation);
                if (options.Tile)
                    for (var x = 0d; x < document.Pages[index].Width.Point; x += Math.Max(size.Width + 40, 80))
                        for (var y = 0d; y < document.Pages[index].Height.Point; y += Math.Max(size.Height + 40, 80))
                            DrawText(graphics, text, font, brush, new XPoint(x, y), size, options.Rotation);
            }
            var pageNumber = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var totalPages = document.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var formattedPageNumber = (options.PageNumberFormat ?? "{page}/{pages}").Replace("{page}", pageNumber).Replace("{pages}", totalPages);
            var header = options.Header?.Replace("{page}", pageNumber).Replace("{pages}", totalPages);
            var footer = options.Footer?.Replace("{page}", pageNumber).Replace("{pages}", totalPages);
            if (string.IsNullOrWhiteSpace(footer) && !string.IsNullOrWhiteSpace(options.PageNumberFormat)) footer = formattedPageNumber;
            if (!string.IsNullOrWhiteSpace(header)) graphics.DrawString(header, font, brush, new XRect(12, 8, document.Pages[index].Width.Point - 24, 24), XStringFormats.TopCenter);
            if (!string.IsNullOrWhiteSpace(footer)) graphics.DrawString(footer, font, brush, new XRect(12, document.Pages[index].Height.Point - 30, document.Pages[index].Width.Point - 24, 24), XStringFormats.BottomCenter);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        document.Save(outputPath);
    }

    private static string PrepareImage(string path, double opacity)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"inactivepdf-watermark-{Guid.NewGuid():N}.png");
        using var image = new MagickImage(path);
        image.AutoOrient();
        image.Alpha(AlphaOption.Set);
        image.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, opacity);
        image.Write(temporary, MagickFormat.Png);
        return temporary;
    }

    private static void DrawImage(XGraphics graphics, XImage image, XPoint point, double width, double height, double rotation, bool tile, double pageWidth, double pageHeight)
    {
        void Draw(XPoint p)
        {
            graphics.Save();
            graphics.TranslateTransform(p.X + width / 2, p.Y + height / 2);
            graphics.RotateTransform(rotation);
            graphics.DrawImage(image, -width / 2, -height / 2, width, height);
            graphics.Restore();
        }
        Draw(point);
        if (!tile) return;
        for (var x = 0d; x < pageWidth; x += Math.Max(width + 40, 80))
            for (var y = 0d; y < pageHeight; y += Math.Max(height + 40, 80)) Draw(new XPoint(x, y));
    }

    private static void DrawText(XGraphics graphics, string text, XFont font, XBrush brush, XPoint point, XSize size, double rotation)
    {
        graphics.Save();
        graphics.TranslateTransform(point.X + size.Width / 2, point.Y + size.Height / 2);
        graphics.RotateTransform(rotation);
        graphics.DrawString(text, font, brush, -size.Width / 2, -size.Height / 2, XStringFormats.Default);
        graphics.Restore();
    }

    private static XColor ParseColor(string value, double opacity)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) throw new ArgumentException("Color must be a six-digit hexadecimal value.", nameof(value));
        return XColor.FromArgb((byte)Math.Round(opacity * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static XPoint Position(WatermarkPosition position, double pageWidth, double pageHeight, double width, double height, double x, double y) => position switch
    {
        WatermarkPosition.TopLeft => new(x, y),
        WatermarkPosition.TopCenter => new((pageWidth - width) / 2 + x, y),
        WatermarkPosition.TopRight => new(pageWidth - width + x, y),
        WatermarkPosition.CenterLeft => new(x, (pageHeight - height) / 2 + y),
        WatermarkPosition.Center => new((pageWidth - width) / 2 + x, (pageHeight - height) / 2 + y),
        WatermarkPosition.CenterRight => new(pageWidth - width + x, (pageHeight - height) / 2 + y),
        WatermarkPosition.BottomLeft => new(x, pageHeight - height + y),
        WatermarkPosition.BottomCenter => new((pageWidth - width) / 2 + x, pageHeight - height + y),
        _ => new(pageWidth - width + x, pageHeight - height + y)
    };

    private static bool Includes(string pages, int page, int total)
    {
        var normalized = pages.Trim().ToLowerInvariant();
        if (normalized == "all") return true;
        if (normalized == "none") return false;
        if (normalized == "first") return page == 1;
        if (normalized == "last") return page == total;
        if (normalized == "odd") return page % 2 == 1;
        if (normalized == "even") return page % 2 == 0;
        return WatermarkProfileValidator.TryParsePages(normalized, out var selected) && selected.Contains(page);
    }
}
