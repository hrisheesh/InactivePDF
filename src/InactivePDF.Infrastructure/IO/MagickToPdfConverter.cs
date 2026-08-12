using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.Validation;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace InactivePDF.Infrastructure.IO;

public sealed class MagickToPdfConverter : IImageToPdfConverter
{
    private const double PointsPerInch = 72d;
    private const double DefaultDpi = 96d;
    private readonly long _maximumPixels = ResolveMaximumPixels();

    public MagickToPdfConverter()
    {
        // ImageMagick is isolated in the conversion worker. These limits keep a malformed
        // decompression bomb from reserving an unbounded pixel cache before our per-frame check.
        ResourceLimits.Area = (ulong)_maximumPixels;
        ResourceLimits.Width = (ulong)_maximumPixels;
        ResourceLimits.Height = (ulong)_maximumPixels;
        ResourceLimits.ListLength = 512;
    }

    public ImageConversionResult Convert(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (inputPaths.Count == 0) throw new ArgumentException("At least one image is required.", nameof(inputPaths));

        var resolvedProfile = profile ?? PdfOutputProfileCatalog.Resolve("archive");
        var inputs = inputPaths.Select(ValidateInput).ToArray();
        var output = ValidateOutput(outputPath);
        var temporary = Path.Combine(
            Path.GetDirectoryName(output)!,
            $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.tmp.pdf");

        using var document = new PdfDocument();
        try
        {
            ConfigureDocument(document, resolvedProfile);
            foreach (var input in inputs) AddImagePages(document, input, resolvedProfile);
            var pageCount = document.PageCount;
            document.Save(temporary);
            File.Move(temporary, output, overwrite: true);
            return new ImageConversionResult(output, pageCount, new FileInfo(output).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void AddImagePages(PdfDocument document, string path, PdfOutputProfile profile)
    {
        var format = SupportedFormatCatalog.GetRequired(Path.GetExtension(path));
        _ = InputFormatValidator.Validate(path, Path.GetFileName(path));
        if (profile.PreserveJpegData && format.Extension is (".jpg" or ".jpeg") && IsNormalOrientation(path))
        {
            AddPreservedJpegPage(document, path);
            return;
        }

        using var frames = new MagickImageCollection(path);
        var frameIndex = 0;
        foreach (var frame in frames)
        {
            frame.AutoOrient();
            var pixels = checked((long)frame.Width * frame.Height);
            if (pixels > _maximumPixels)
                throw new InvalidDataException($"Image '{path}' frame {frameIndex} contains {pixels} pixels, exceeding the configured limit of {_maximumPixels}.");

            if (profile.DownsampleImages && profile.MaximumImageDpi > 0)
                Downsample(frame, profile.MaximumImageDpi);

            using var encoded = new MemoryStream();
            if (profile.DownsampleImages && !frame.HasAlpha)
            {
                frame.Quality = (uint)Math.Clamp(profile.JpegQuality, 1, 100);
                frame.Write(encoded, MagickFormat.Jpeg);
            }
            else
            {
                frame.Write(encoded, MagickFormat.Png);
            }
            encoded.Position = 0;

            var page = document.AddPage();
            page.Width = XUnit.FromPoint(ToPoints((int)frame.Width, frame.Density.X));
            page.Height = XUnit.FromPoint(ToPoints((int)frame.Height, frame.Density.Y));
            using var xImage = XImage.FromStream(encoded);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
            frameIndex++;
        }

        if (frameIndex == 0) throw new InvalidDataException($"Image '{path}' contains no frames.");
    }

    private static void AddPreservedJpegPage(PdfDocument document, string path)
    {
        using var image = XImage.FromFile(path);
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(ToPoints(image.PixelWidth, image.HorizontalResolution));
        page.Height = XUnit.FromPoint(ToPoints(image.PixelHeight, image.VerticalResolution));
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
    }

    private static void Downsample(IMagickImage<byte> frame, int maximumDpi)
    {
        var dpi = frame.Density.X > 0 ? frame.Density.X : DefaultDpi;
        if (dpi <= maximumDpi) return;
        var scale = maximumDpi / dpi;
        var width = Math.Max(1u, (uint)Math.Round(frame.Width * scale));
        var height = Math.Max(1u, (uint)Math.Round(frame.Height * scale));
        frame.Resize(width, height);
        frame.Density = new Density(maximumDpi, maximumDpi);
    }

    private static bool IsNormalOrientation(string path)
    {
        using var image = new MagickImage(path);
        var orientation = image.Orientation.ToString();
        return orientation.Equals("Undefined", StringComparison.OrdinalIgnoreCase) || orientation.Equals("TopLeft", StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigureDocument(PdfDocument document, PdfOutputProfile profile) =>
        PdfProfileDocumentConfigurator.Apply(document, profile);

    private static double ToPoints(int pixels, double dpi) =>
        pixels / (dpi > 0 ? dpi : DefaultDpi) * PointsPerInch;

    private static string ValidateInput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The image does not exist.", path);
        if (!SupportedFormatCatalog.IsImage(Path.GetExtension(fullPath)))
            throw new NotSupportedException($"Unsupported image extension: {Path.GetExtension(fullPath)}");
        return fullPath;
    }

    private static string ValidateOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must have a .pdf extension.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static long ResolveMaximumPixels() =>
        long.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_MAX_IMAGE_PIXELS"), out var value) && value > 0
            ? value
            : 50_000_000;
}
