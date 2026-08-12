using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.IO;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Keeps the public API and queued-job image path on the same bounded process
/// boundary as the watch-folder path.
/// </summary>
public sealed class IsolatedImageToPdfConverter(IsolatedConversionWorker worker) : IImageToPdfConverter
{
    public ImageConversionResult Convert(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (inputPaths.Count == 0) throw new ArgumentException("At least one image is required.", nameof(inputPaths));

        var resolvedProfile = profile ?? PdfOutputProfileCatalog.Resolve("archive");
        var temporaryOutputs = new List<string>();
        try
        {
            for (var index = 0; index < inputPaths.Count; index++)
            {
                var input = Path.GetFullPath(inputPaths[index]);
                var destination = inputPaths.Count == 1
                    ? outputPath
                    : Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileNameWithoutExtension(outputPath)}-{Guid.NewGuid():N}-{index:D4}.pdf");
                worker.ConvertAsync(
                    new ConversionWorkerRequest(
                        ConversionOperation.ConvertFile,
                        destination,
                        [new ConversionWorkerInput(input, Path.GetFileName(input), "application/octet-stream")],
                        resolvedProfile.Name))
                    .GetAwaiter()
                    .GetResult();
                if (inputPaths.Count > 1) temporaryOutputs.Add(destination);
            }

            if (temporaryOutputs.Count > 0) new PdfSharpOperations().Merge(temporaryOutputs, outputPath);
        }
        finally
        {
            foreach (var temporary in temporaryOutputs)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        var inspection = new PdfSharpOperations().Inspect(outputPath);
        return new ImageConversionResult(outputPath, inspection.PageCount, new FileInfo(outputPath).Length);
    }
}
