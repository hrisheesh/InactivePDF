using InactivePDF.Application.Models;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Jobs;

public sealed record ConversionConcurrencyOptions(
    int OfficeConcurrency = 1,
    int ImageConcurrency = 1,
    int PdfConcurrency = 1,
    int TextConcurrency = 2)
{
    public static ConversionConcurrencyOptions FromEnvironment() => new(
        ParsePositive("INACTIVEPDF_OFFICE_CONCURRENCY", 1),
        ParsePositive("INACTIVEPDF_IMAGE_CONCURRENCY", 1),
        ParsePositive("INACTIVEPDF_PDF_CONCURRENCY", 1),
        ParsePositive("INACTIVEPDF_TEXT_CONCURRENCY", 2));

    private static int ParsePositive(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}

public sealed class ConversionConcurrencyGate(ConversionConcurrencyOptions options) : IDisposable
{
    private readonly SemaphoreSlim _office = new(options.OfficeConcurrency, options.OfficeConcurrency);
    private readonly SemaphoreSlim _image = new(options.ImageConcurrency, options.ImageConcurrency);
    private readonly SemaphoreSlim _pdf = new(options.PdfConcurrency, options.PdfConcurrency);
    private readonly SemaphoreSlim _text = new(options.TextConcurrency, options.TextConcurrency);

    public async ValueTask<ConversionConcurrencyLease> AcquireAsync(ConversionWorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = ResolveKind(item);
        var semaphore = kind switch
        {
            ConversionWorkKind.Office => _office,
            ConversionWorkKind.Image => _image,
            ConversionWorkKind.Pdf => _pdf,
            _ => _text
        };
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversionConcurrencyLease(semaphore);
    }

    public void Dispose()
    {
        _office.Dispose();
        _image.Dispose();
        _pdf.Dispose();
        _text.Dispose();
    }

    private static ConversionWorkKind ResolveKind(ConversionWorkItem item)
    {
        if (item.Request.Operation == ConversionOperation.CreateTextPdf) return ConversionWorkKind.Text;
        if (item.Request.Operation is ConversionOperation.ConvertAndMerge or ConversionOperation.ConvertFiles) return ConversionWorkKind.Pdf;

        var extension = item.Inputs.Count == 0 ? string.Empty : Path.GetExtension(item.Inputs[0].FileName);
        if (SupportedFormatCatalog.TryGet(extension, out var format))
        {
            if (format.Category == ConversionFormatCategory.Pdf) return ConversionWorkKind.Pdf;
            if (format.Category == ConversionFormatCategory.Image) return ConversionWorkKind.Image;
            if (format.Route == ConversionFormatRoute.TextGenerator) return ConversionWorkKind.Text;
        }

        return ConversionWorkKind.Office;
    }

    private enum ConversionWorkKind
    {
        Office,
        Image,
        Pdf,
        Text
    }
}

public sealed class ConversionConcurrencyLease(SemaphoreSlim semaphore) : IAsyncDisposable
{
    private int _disposed;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release();
        return ValueTask.CompletedTask;
    }
}
