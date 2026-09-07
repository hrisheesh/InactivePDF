using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Api;

internal static class CompatibilityEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/convert-file", async (IFormFile file, string? profile, string? watermarkProfile, string? watermark, IsolatedConversionService service, ApiRequestLimits limits, CancellationToken cancellationToken) =>
        {
            if (file.Length > limits.MaximumFileBytes)
                return Results.Problem($"The file must be at most {limits.MaximumFileBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
            await using var input = file.OpenReadStream();
            var output = await service.ConvertFileAsync(input, file.FileName, file.ContentType, profile ?? DefaultProfile(), watermarkProfile, ParseWatermark(watermark), cancellationToken).ConfigureAwait(false);
            return new ConversionFileResult(output);
        })
            .DisableAntiforgery();

        app.MapPost("/v1/create-text-pdf", async (TextPdfRequest request, string? watermarkProfile, IsolatedConversionService service, CancellationToken cancellationToken) =>
        {
            var output = await service.CreateTextPdfAsync(request.Text ?? string.Empty, request.Profile ?? DefaultProfile(), watermarkProfile, request.Watermark, cancellationToken).ConfigureAwait(false);
            return new ConversionFileResult(output);
        });

        app.MapPost("/v1/convert-and-merge", async (IFormFileCollection files, string? profile, string? watermarkProfile, string? watermark, IsolatedConversionService service, ApiRequestLimits limits, CancellationToken cancellationToken) =>
        {
            if (files.Count > limits.MaximumFiles)
                return Results.Problem($"A request cannot contain more than {limits.MaximumFiles} files.", statusCode: StatusCodes.Status413PayloadTooLarge);
            if (files.Any(file => file.Length > limits.MaximumFileBytes))
                return Results.Problem($"Each file must be at most {limits.MaximumFileBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
            var inputs = files.Select(file => (file.OpenReadStream(), file.FileName, file.ContentType)).ToArray();
            try { return new ConversionFileResult(await service.ConvertAndMergeAsync(inputs, profile ?? DefaultProfile(), watermarkProfile, ParseWatermark(watermark), cancellationToken).ConfigureAwait(false)); }
            finally { foreach (var input in inputs) input.Item1.Dispose(); }
        }).DisableAntiforgery();
    }

    private sealed record TextPdfRequest(string? Text, string? Profile, InactivePDF.Domain.Models.WatermarkOptions? Watermark);

    private static InactivePDF.Domain.Models.WatermarkOptions? ParseWatermark(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : System.Text.Json.JsonSerializer.Deserialize<InactivePDF.Domain.Models.WatermarkOptions>(value);

    private static string DefaultProfile() => Environment.GetEnvironmentVariable("INACTIVEPDF_DEFAULT_PROFILE") ?? "archive";
}
