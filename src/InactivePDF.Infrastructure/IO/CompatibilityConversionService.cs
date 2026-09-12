using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Rendering;
using InactivePDF.Infrastructure.Validation;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Infrastructure.IO;

/// <summary>
/// Conversion engine used only inside InactivePDF.ConversionWorker. The API process
/// reaches it through <see cref="IsolatedConversionWorker"/> and never invokes it directly.
/// </summary>
public sealed class CompatibilityConversionService(
    IImageToPdfConverter imageConverter,
    ITextPdfGenerator textGenerator,
    IOfficeDocumentConverter officeConverter,
    IPdfOperations pdfOperations,
    ResourcePolicy resourcePolicy,
    RtfFastPathConverter? rtfFastPathConverter = null,
    ConversionStageRecorder? stageRecorder = null)
{
    public async Task ConvertRequestToFileAsync(
        ConversionWorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var profile = PdfOutputProfileCatalog.Resolve(request.Profile);
        var recorder = request.ExecutionMode == ConversionExecutionMode.Development ? stageRecorder : null;

        var output = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporaryOutput = Path.Combine(
            Path.GetDirectoryName(output)!,
            $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.partial.pdf");

        try
        {
            using (recorder?.Measure("conversion"))
                await ConvertRequestToPathAsync(request, temporaryOutput, profile, recorder, cancellationToken).ConfigureAwait(false);
            if (profile.StructuralValidation || request.ExecutionMode == ConversionExecutionMode.Production)
            {
                using (recorder?.Measure("requiredValidation"))
                    _ = pdfOperations.Inspect(temporaryOutput);
            }
            long outputLength;
            using (recorder?.Measure("pdfWrite"))
                outputLength = new FileInfo(temporaryOutput).Length;
            if (outputLength > resourcePolicy.MaximumOutputBytes)
                throw new IOException($"The conversion output exceeds the maximum allowed size of {resourcePolicy.MaximumOutputBytes} bytes.");
            using (recorder?.Measure("publish"))
                PublishAtomically(temporaryOutput, output);
        }
        finally
        {
            using (recorder?.Measure("cleanup"))
                TryDelete(temporaryOutput);
        }
    }

    private async Task ConvertRequestToPathAsync(
        ConversionWorkerRequest request,
        string outputPath,
        PdfOutputProfile profile,
        ConversionStageRecorder? recorder,
        CancellationToken cancellationToken)
    {
        if (request.Operation == ConversionOperation.CreateTextPdf)
        {
            var textInput = request.Inputs[0];
            string text;
            using (recorder?.Measure("inputLoad"))
                text = await File.ReadAllTextAsync(textInput.Path, cancellationToken).ConfigureAwait(false);
            textGenerator.Create(text, outputPath, new TextPdfOptions(OutputProfile: profile));
            return;
        }

        if (request.Operation == ConversionOperation.ConvertFile)
        {
            await ConvertPathAsync(request.Inputs[0].Path, request.Inputs[0].FileName, request.Inputs[0].ContentType, outputPath, profile, request.ExecutionMode, recorder, cancellationToken).ConfigureAwait(false);
            return;
        }

        var convertedPaths = new List<string>(request.Inputs.Count);
        try
        {
            for (var index = 0; index < request.Inputs.Count; index++)
            {
                var input = request.Inputs[index];
                var converted = Path.Combine(
                    Path.GetDirectoryName(outputPath)!,
                    $".{Path.GetFileNameWithoutExtension(outputPath)}-{index:D4}-{Guid.NewGuid():N}.part.pdf");
                await ConvertPathAsync(input.Path, input.FileName, input.ContentType, converted, profile, request.ExecutionMode, recorder, cancellationToken).ConfigureAwait(false);
                convertedPaths.Add(converted);
            }

            pdfOperations.Merge(convertedPaths, outputPath, profile);
        }
        finally
        {
            foreach (var path in convertedPaths) TryDelete(path);
        }
    }

    private async Task ConvertPathAsync(
        string source,
        string fileName,
        string contentType,
        string output,
        PdfOutputProfile profile,
        ConversionExecutionMode executionMode,
        ConversionStageRecorder? recorder,
        CancellationToken cancellationToken)
    {
        InputFormatValidation validation;
        using (recorder?.Measure("inputLoad"))
        validation = InputFormatValidator.Validate(source, fileName, contentType, executionMode);
        if (validation.Format.Route == ConversionFormatRoute.PdfPassThrough)
        {
            if (executionMode == ConversionExecutionMode.Development)
                _ = pdfOperations.Inspect(source);
            File.Copy(source, output, overwrite: true);
            if (!profile.Name.Equals("archive", StringComparison.OrdinalIgnoreCase))
                pdfOperations.ApplyProfile(output, profile);
        }
        else if (validation.Format.Route == ConversionFormatRoute.Image)
        {
            imageConverter.Convert([source], output, profile);
        }
        else if (validation.Format.Route == ConversionFormatRoute.TextGenerator)
        {
            textGenerator.Create(await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false), output, new TextPdfOptions(OutputProfile: profile));
        }
        else if (validation.Format.Extension == ".rtf" && rtfFastPathConverter?.TryConvert(source, output, profile, cancellationToken) is true)
        {
            return;
        }
        else
        {
            await officeConverter.ConvertAsync(source, output, profile, cancellationToken).ConfigureAwait(false);
            if (!profile.PreserveSourceMetadata || profile.PdfVersion is < 15 or > 17)
                pdfOperations.ApplyProfile(output, profile);
        }
    }

    private static void ValidateRequest(ConversionWorkerRequest request)
    {
        if (request.Inputs.Count == 0) throw new ArgumentException("At least one worker input is required.", nameof(request));
        if (request.Operation == ConversionOperation.ConvertFile && request.Inputs.Count != 1)
            throw new ArgumentException("ConvertFile requires exactly one input.", nameof(request));
        if (request.Operation == ConversionOperation.CreateTextPdf && request.Inputs.Count != 1)
            throw new ArgumentException("CreateTextPdf requires exactly one text input.", nameof(request));

        foreach (var input in request.Inputs)
        {
            WorkspacePathSecurity.EnsureSafeChain(Path.GetFullPath(input.Path), Path.GetDirectoryName(Path.GetFullPath(input.Path))!);
            if (!File.Exists(input.Path)) throw new FileNotFoundException("The worker input does not exist.", input.Path);
            if (string.IsNullOrWhiteSpace(input.FileName)) throw new ArgumentException("A worker input filename is required.", nameof(request));
        }
        WorkspacePathSecurity.EnsureSafeChain(Path.GetFullPath(request.OutputPath), Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
    }

    private static void PublishAtomically(string temporaryPath, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            try
            {
                File.Replace(temporaryPath, outputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
