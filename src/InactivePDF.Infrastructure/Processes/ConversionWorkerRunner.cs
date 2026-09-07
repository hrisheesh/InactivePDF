using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Rendering;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Application.Capabilities;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InactivePDF.Infrastructure.Processes;

using InactivePDF.Infrastructure.Resources;

public static class ConversionWorkerRunner
{
    public const string Switch = "--conversion-worker";
    public const string EnvironmentVariable = "INACTIVEPDF_CONVERSION_WORKER";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool IsWorker(IReadOnlyList<string> args) => args.Contains(Switch, StringComparer.Ordinal);

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        try
        {
            InactivePdfSettings.LoadAndApply();
            var requestPath = Required(args, "--request");
            var request = await ReadRequestAsync(requestPath).ConfigureAwait(false);
            var converter = new CompatibilityConversionService(
                new MagickToPdfConverter(ResourcePolicy.FromEnvironment()),
                new PdfTextGenerator(),
                new LibreOfficeConverter(LibreOfficeOptions.Default),
                new PdfSharpOperations(),
                ResourcePolicy.FromEnvironment(),
                new RtfFastPathConverter(new PdfTextGenerator()));
            await converter.ConvertRequestToFileAsync(request).ConfigureAwait(false);
            var watermark = request.Watermark;
            if (watermark is null && !string.IsNullOrWhiteSpace(request.WatermarkProfile))
            {
                var profilePath = Environment.GetEnvironmentVariable("INACTIVEPDF_WATERMARK_PROFILES_PATH");
                if (!string.IsNullOrWhiteSpace(profilePath) && File.Exists(profilePath))
                {
                    await using var profileStream = File.OpenRead(profilePath);
                    var profiles = await JsonSerializer.DeserializeAsync<Dictionary<string, WatermarkOptions>>(profileStream).ConfigureAwait(false);
                    profiles?.TryGetValue(request.WatermarkProfile, out watermark);
                }
            }
            if (watermark is not null)
            {
                watermark = ResolveWatermarkAsset(watermark);
                var temporary = request.OutputPath + ".watermark.tmp";
                new PdfWatermarkService().Apply(request.OutputPath, temporary, watermark);
                File.Move(temporary, request.OutputPath, overwrite: true);
            }
            return 0;
        }
        catch (ConversionFormatException exception)
        {
            Console.Error.WriteLine($"INACTIVEPDF_ERROR_CODE={exception.Code}");
            Console.Error.WriteLine(exception);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static WatermarkOptions ResolveWatermarkAsset(WatermarkOptions options)
    {
        if (options.Kind != WatermarkKind.Image || string.IsNullOrWhiteSpace(options.ImagePath)) return options;
        var assetRoot = Environment.GetEnvironmentVariable("INACTIVEPDF_WATERMARK_ASSET_PATH");
        if (string.IsNullOrWhiteSpace(assetRoot)) throw new UnauthorizedAccessException("Watermark assets are not configured.");
        return WatermarkAssetResolver.Resolve(options, assetRoot);
    }

    private static async Task<ConversionWorkerRequest> ReadRequestAsync(string path)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<ConversionWorkerRequest>(stream, JsonOptions).ConfigureAwait(false)
            ?? throw new InvalidDataException("The conversion worker request manifest was empty.");
    }

    private static string Required(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[index + 1]))
                return args[index + 1];
        }

        throw new ArgumentException($"The conversion worker requires {name}.");
    }
}
