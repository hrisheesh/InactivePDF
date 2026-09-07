using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Adapts durable API jobs to the same isolated worker used by synchronous and watch-folder calls.
/// </summary>
public sealed class IsolatedConversionWorkProcessor(IsolatedConversionWorker worker, LibreOfficeSessionHost officeSession) : IConversionWorkProcessor
{
    public async Task<string> ProcessAsync(ConversionWorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var output = Path.Combine(item.WorkspacePath, "result.pdf");
        var request = new ConversionWorkerRequest(
            item.Request.Operation,
            output,
            item.Inputs.Select(input => new ConversionWorkerInput(input.Path, input.FileName, input.ContentType)).ToArray(),
            item.Request.Options.Profile);

        IAsyncDisposable? officeLease = null;
        try
        {
            if (item.Inputs.Any(input => SupportedFormatCatalog.TryGet(Path.GetExtension(input.FileName), out var format) && format.Route == ConversionFormatRoute.LibreOffice))
                officeLease = await officeSession.AcquireAsync(cancellationToken).ConfigureAwait(false);

            await worker.ConvertAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (officeLease is not null) await officeLease.DisposeAsync().ConfigureAwait(false);
        }
        return output;
    }
}
