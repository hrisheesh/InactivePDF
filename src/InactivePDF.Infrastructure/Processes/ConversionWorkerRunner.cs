using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Rendering;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Application.Capabilities;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InactivePDF.Infrastructure.Processes;

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
