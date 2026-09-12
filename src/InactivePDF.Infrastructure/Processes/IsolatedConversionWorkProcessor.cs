using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Adapts durable API jobs to the same isolated worker used by synchronous and watch-folder calls.
/// </summary>
public sealed class IsolatedConversionWorkProcessor(IsolatedConversionWorker worker) : IConversionWorkProcessor
{
    public async Task<string> ProcessAsync(ConversionWorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var output = Path.Combine(item.WorkspacePath, "result.pdf");
        var request = new ConversionWorkerRequest(
            item.Request.Operation,
            output,
            item.Inputs.Select(input => new ConversionWorkerInput(input.Path, input.FileName, input.ContentType)).ToArray(),
            item.Request.Options.Profile, item.Request.Options.Watermark, item.Request.Options.WatermarkProfile,
            Source: "Queued", JobId: item.Job.Id.ToString(), ExecutionMode: ConversionExecutionModeParser.FromEnvironment());

        await worker.ConvertAsync(request, cancellationToken).ConfigureAwait(false);
        return output;
    }
}
