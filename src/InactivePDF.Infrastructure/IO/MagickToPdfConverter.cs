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
    private readonly ResourcePolicy _resourcePolicy;

    public MagickToPdfConverter(ResourcePolicy? resourcePolicy = null)
    {
        _resourcePolicy = resourcePolicy ?? ResourcePolicy.FromEnvironment();

        // ImageMagick is isolated in the conversion worker. The pixel-cache limits are
        // deliberately stricter than ImageMagick's machine-relative defaults so a single
        // hostile image cannot consume the server. Excess pixels spill to the configured
        // map/disk budgets instead of growing the process without a bound.
        ResourceLimits.Area = checked((ulong)_resourcePolicy.MaximumImagePixels);
        ResourceLimits.Width = checked((ulong)_resourcePolicy.MaximumImageWidth);
        ResourceLimits.Height = checked((ulong)_resourcePolicy.MaximumImageHeight);
        ResourceLimits.ListLength = checked((ulong)_resourcePolicy.MaximumImageFrames);
        ResourceLimits.Memory = checked((ulong)_resourcePolicy.ImageMemoryBytes);
        ResourceLimits.Disk = checked((ulong)_resourcePolicy.ImageDiskBytes);
        ResourceLimits.MaxMemoryRequest = checked((ulong)_resourcePolicy.ImageMemoryBytes);
        ResourceLimits.Time = checked((ulong)Math.Max(1, _resourcePolicy.MaximumDuration.TotalSeconds));
        if (_resourcePolicy.ImageThreadCount > 0)
            ResourceLimits.Thread = checked((ulong)_resourcePolicy.ImageThreadCount);
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
        try
        {
            var format = SupportedFormatCatalog.GetRequired(Path.GetExtension(path));
            _ = InputFormatValidator.Validate(path, Path.GetFileName(path));
            if (profile.PreserveJpegData && format.Extension is (".jpg" or ".jpeg") && IsNormalOrientation(path))
            {
                try
                {
                    AddPreservedJpegPage(document, path);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // PDFsharp Core intentionally supports only common JPEG variants.
                    // Decode through ImageMagick and embed a lossless PNG fallback instead
                    // of rejecting a valid JPEG or silently re-encoding it to JPEG.
                }
                catch (ArgumentException)
                {
                    // Treat other PDFsharp image-parser incompatibilities the same way.
                }
                catch (NotSupportedException)
                {
                    // Keep valid but unsupported Core image variants on the decoded path.
                }
            }

            AddDecodedImagePages(document, path, profile);
        }
        catch (ConversionFormatException)
        {
            throw;
        }
        catch (MagickException exception)
        {
            throw new ConversionFormatException(
                "invalid_image",
                $"The image '{Path.GetFileName(path)}' could not be decoded safely: {exception.Message}",
                exception);
        }
    }

    private void AddDecodedImagePages(PdfDocument document, string path, PdfOutputProfile profile)
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".gif" or ".tif" or ".tiff"))
        {
            using var image = new MagickImage(path);
            AddDecodedFramePage(document, image, path, 0, profile);
            return;
        }

        using var frames = new MagickImageCollection(path);
        if (frames.Count == 1)
        {
            using var image = frames[0].Clone();
            frames.Clear();
            AddDecodedFramePage(document, image, path, 0, profile);
            return;
        }

        var frameIndex = 0;
        foreach (var frame in frames)
        {
            if (frameIndex >= _resourcePolicy.MaximumImageFrames)
                throw new InvalidDataException($"Image '{path}' contains more than {_resourcePolicy.MaximumImageFrames} frames.");

            AddDecodedFramePage(document, frame, path, frameIndex, profile);
            frameIndex++;
        }

        if (frameIndex == 0) throw new InvalidDataException($"Image '{path}' contains no frames.");
    }

    private void AddDecodedFramePage(PdfDocument document, IMagickImage<byte> frame, string path, int frameIndex, PdfOutputProfile profile)
    {
        frame.AutoOrient();
        var pixels = checked((long)frame.Width * frame.Height);
        if (pixels > _resourcePolicy.MaximumImagePixels)
            throw new InvalidDataException($"Image '{path}' frame {frameIndex} contains {pixels} pixels, exceeding the configured limit of {_resourcePolicy.MaximumImagePixels}.");
        if (frame.Width > _resourcePolicy.MaximumImageWidth || frame.Height > _resourcePolicy.MaximumImageHeight)
            throw new InvalidDataException($"Image '{path}' frame {frameIndex} has dimensions {frame.Width}x{frame.Height}, exceeding the configured limit of {_resourcePolicy.MaximumImageWidth}x{_resourcePolicy.MaximumImageHeight}.");

        if (profile.DownsampleImages && profile.MaximumImageDpi > 0)
            Downsample(frame, profile.MaximumImageDpi);

        var outputFormat = profile.DownsampleImages && !frame.HasAlpha ? MagickFormat.Jpeg : MagickFormat.Png;
        var temporary = Path.Combine(Path.GetTempPath(), $"inactivepdf-image-{Guid.NewGuid():N}{(outputFormat == MagickFormat.Jpeg ? ".jpg" : ".png")}");
        var width = frame.Width;
        var height = frame.Height;
        var densityX = frame.Density.X;
        var densityY = frame.Density.Y;
        try
        {
            if (outputFormat == MagickFormat.Jpeg)
                frame.Quality = (uint)Math.Clamp(profile.JpegQuality, 1, 100);
            frame.Write(temporary, outputFormat);

            var page = document.AddPage();
            page.Width = XUnit.FromPoint(ToPoints((int)width, densityX));
            page.Height = XUnit.FromPoint(ToPoints((int)height, densityY));
            using var xImage = XImage.FromFile(temporary);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        using var images = new MagickImageCollection();
        images.Ping(path);
        if (images.Count == 0) return false;
        var orientation = images[0].Orientation.ToString();
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

}
