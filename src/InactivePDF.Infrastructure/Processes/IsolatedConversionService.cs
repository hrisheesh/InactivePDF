#pragma warning disable CA1822
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Resources;
using System.Text;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Owns short-lived API conversion workspaces and streams completed output without
/// materializing the complete PDF in the API process.
/// </summary>
public sealed class IsolatedConversionService(
    IsolatedConversionWorker worker,
    IJobWorkspaceFactory workspaceFactory,
    IBoundedStreamCopier copier,
    IDiskSpaceGuard diskSpaceGuard,
    ResourcePolicy resourcePolicy)
{
    public Task<IsolatedConversionOutput> ConvertFileAsync(
        Stream input,
        string fileName,
        CancellationToken cancellationToken = default) =>
        ConvertFileAsync(input, fileName, "application/octet-stream", DefaultProfile(), null, null, cancellationToken);

    public Task<IsolatedConversionOutput> ConvertFileAsync(
        Stream input,
        string fileName,
        string profile,
        CancellationToken cancellationToken = default) =>
        ConvertFileAsync(input, fileName, "application/octet-stream", profile, null, null, cancellationToken);

    public Task<IsolatedConversionOutput> ConvertFileAsync(
        Stream input,
        string fileName,
        string contentType,
        string profile,
        string? watermarkProfile = null, WatermarkOptions? watermark = null, CancellationToken cancellationToken = default) =>
        ConvertAsync([(input, fileName, contentType)], ConversionOperation.ConvertFile, "converted.pdf", profile, cancellationToken, watermarkProfile, watermark);

    public Task<IsolatedConversionOutput> ConvertAndMergeAsync(
        IReadOnlyList<(Stream Content, string FileName)> inputs,
        string? watermarkProfile = null, WatermarkOptions? watermark = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
        return ConvertAsync(inputs.Select(input => (input.Content, input.FileName, "application/octet-stream")).ToArray(), ConversionOperation.ConvertAndMerge, "merged.pdf", DefaultProfile(), cancellationToken, watermarkProfile, watermark);
    }

    public Task<IsolatedConversionOutput> ConvertAndMergeAsync(
        IReadOnlyList<(Stream Content, string FileName)> inputs,
        string profile,
        string? watermarkProfile = null, WatermarkOptions? watermark = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
        return ConvertAsync(inputs.Select(input => (input.Content, input.FileName, "application/octet-stream")).ToArray(), ConversionOperation.ConvertAndMerge, "merged.pdf", profile, cancellationToken, watermarkProfile, watermark);
    }

    public Task<IsolatedConversionOutput> ConvertAndMergeAsync(
        IReadOnlyList<(Stream Content, string FileName, string ContentType)> inputs,
        string profile,
        string? watermarkProfile = null,
        WatermarkOptions? watermark = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
        return ConvertAsync(inputs, ConversionOperation.ConvertAndMerge, "merged.pdf", profile, cancellationToken, watermarkProfile, watermark);
    }

    public Task<IsolatedConversionOutput> CreateTextPdfAsync(
        string text,
        CancellationToken cancellationToken = default)
        => CreateTextPdfAsync(text, DefaultProfile(), null, null, cancellationToken);

    public async Task<IsolatedConversionOutput> CreateTextPdfAsync(
        string text,
        string profile,
        string? watermarkProfile = null, WatermarkOptions? watermark = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > resourcePolicy.MaximumInputBytes)
            throw new IOException($"The text input exceeds the maximum allowed size of {resourcePolicy.MaximumInputBytes} bytes.");
        var workspace = await workspaceFactory.CreateAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        try
        {
            diskSpaceGuard.EnsureAvailable(workspace.Workspace.RootPath, resourcePolicy.MinimumFreeDiskBytes);
            var inputPath = Path.Combine(workspace.Workspace.InputPath, "body.txt");
            await File.WriteAllTextAsync(inputPath, text, cancellationToken).ConfigureAwait(false);
            var outputPath = Path.Combine(workspace.Workspace.OutputPath, "text.pdf");
            var metrics = await worker.ConvertAsync(
                new ConversionWorkerRequest(
                    ConversionOperation.CreateTextPdf,
                    outputPath,
                    [new ConversionWorkerInput(inputPath, "body.txt", "text/plain")],
                profile, watermark, watermarkProfile, ExecutionMode: ConversionExecutionModeParser.FromEnvironment()),
                cancellationToken).ConfigureAwait(false);
            return OpenOutput(workspace, outputPath, "text.pdf", metrics);
        }
        catch
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IsolatedConversionOutput> ConvertAsync(
        IReadOnlyList<(Stream Content, string FileName, string ContentType)> inputs,
        ConversionOperation operation,
        string downloadName,
        string profile,
        CancellationToken cancellationToken, string? watermarkProfile = null, WatermarkOptions? watermark = null)
    {
        var workspace = await workspaceFactory.CreateAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        try
        {
            diskSpaceGuard.EnsureAvailable(workspace.Workspace.RootPath, resourcePolicy.MinimumFreeDiskBytes);
            var storedInputs = new List<ConversionWorkerInput>(inputs.Count);
            for (var index = 0; index < inputs.Count; index++)
            {
                ArgumentNullException.ThrowIfNull(inputs[index].Content);
                ArgumentException.ThrowIfNullOrWhiteSpace(inputs[index].FileName);
                var safeName = Path.GetFileName(inputs[index].FileName);
                if (string.IsNullOrWhiteSpace(safeName)) throw new ArgumentException("A valid filename is required.", nameof(inputs));
                var path = Path.Combine(workspace.Workspace.InputPath, $"{index:D4}-{safeName}");
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await copier.CopyAsync(inputs[index].Content, destination, resourcePolicy.MaximumInputBytes, resourcePolicy.BufferSizeBytes, cancellationToken).ConfigureAwait(false);
                storedInputs.Add(new ConversionWorkerInput(path, safeName, inputs[index].ContentType));
            }

            var outputPath = Path.Combine(workspace.Workspace.OutputPath, "result.pdf");
            var metrics = await worker.ConvertAsync(new ConversionWorkerRequest(operation, outputPath, storedInputs, profile, watermark, watermarkProfile,
                ExecutionMode: ConversionExecutionModeParser.FromEnvironment()), cancellationToken).ConfigureAwait(false);
            return OpenOutput(workspace, outputPath, downloadName, metrics);
        }
        catch
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IsolatedConversionOutput OpenOutput(JobWorkspaceLease workspace, string outputPath, string downloadName, IsolatedWorkerMetrics metrics)
    {
        var fileInfo = new FileInfo(outputPath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            throw new InvalidOperationException($"The conversion worker produced no output at '{outputPath}'.");

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new IsolatedConversionOutput(stream, fileInfo.Length, downloadName, workspace, metrics);
    }

    private static string DefaultProfile() =>
        Environment.GetEnvironmentVariable("INACTIVEPDF_DEFAULT_PROFILE") ?? "archive";
}
