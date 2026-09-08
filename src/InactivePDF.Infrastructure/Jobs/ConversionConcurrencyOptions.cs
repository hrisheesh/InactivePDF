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
    private readonly ResourceAdmissionGate _resourceAdmission = new(
        ParseBytes("INACTIVEPDF_SWARM_MEMORY_BYTES", ParseBytes("INACTIVEPDF_WORKER_MAX_MEMORY_BYTES", 1_610_612_736)),
        ParsePositive("INACTIVEPDF_WORKER_COUNT", 1));

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
        try
        {
            var reservation = ResolveReservation(item, kind);
            var resource = reservation > 0 ? await _resourceAdmission.AcquireAsync(reservation, cancellationToken).ConfigureAwait(false) : null;
            return new ConversionConcurrencyLease(semaphore, resource);
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    public void Dispose()
    {
        _office.Dispose();
        _image.Dispose();
        _pdf.Dispose();
        _text.Dispose();
        _resourceAdmission.Dispose();
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

    private static long ResolveReservation(ConversionWorkItem item, ConversionWorkKind kind)
    {
        if (kind == ConversionWorkKind.Office) return 1_207_959_552;
        if (kind == ConversionWorkKind.Image) return 1_073_741_824;
        if (item.Inputs.Any(input => input.Path is not null && File.Exists(input.Path) && new FileInfo(input.Path).Length >= 1L * 1024 * 1024))
            return 67_108_864;
        return 0;
    }

    private static int ParsePositive(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static long ParseBytes(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private enum ConversionWorkKind
    {
        Office,
        Image,
        Pdf,
        Text
    }
}

public sealed class ConversionConcurrencyLease(SemaphoreSlim semaphore, ResourceAdmissionLease? resourceLease) : IAsyncDisposable
{
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (resourceLease is not null) await resourceLease.DisposeAsync().ConfigureAwait(false);
            semaphore.Release();
        }
    }
}
