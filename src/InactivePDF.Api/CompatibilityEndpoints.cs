using System.Diagnostics;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Domain.Models;
using System.Text.Json;

namespace InactivePDF.Api;

internal static class CompatibilityEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/convert-file", async (HttpContext context, IFormFile file, string? profile, string? watermarkProfile, string? watermark, IsolatedConversionService service, ApiRequestLimits limits, ApiAdmissionService admission, WatermarkProfileStore watermarkProfiles, CancellationToken cancellationToken) =>
        {
            if (file.Length > limits.MaximumFileBytes)
                return ApiLimitResponses.TooLarge(context, "file_bytes_exceeded", $"The file must be at most {limits.MaximumFileBytes} bytes.", "maximum file bytes", limits.MaximumFileBytes);
            WatermarkOptions? directWatermark;
            try { directWatermark = ConversionApiHelpers.ParseWatermark(watermark); }
            catch (JsonException) { return Results.BadRequest(new { code = "invalid_watermark", message = "The watermark JSON is invalid." }); }
            var resolvedWatermark = ConversionApiHelpers.ResolveWatermark(watermarkProfile, directWatermark, watermarkProfiles, out var watermarkError);
            if (watermarkError is not null) return watermarkError;
            var identity = ApiAuthentication.GetIdentity(context);
            var failure = admission.TryAdmit(identity, new ApiAdmissionRequest(context.Request.ContentLength ?? file.Length, file.Length, file.Length, 1, Queued: false, Format: Path.GetExtension(file.FileName)), out var lease);
            if (failure is not null) return failure.ToResult(context);
            try
            {
                var timer = Stopwatch.StartNew();
                await using var input = file.OpenReadStream();
                var output = await service.ConvertFileAsync(input, file.FileName, file.ContentType, profile ?? DefaultProfile(), watermark: resolvedWatermark, cancellationToken: cancellationToken).ConfigureAwait(false);
                var maximumOutputBytes = admission.GetMaximumOutputBytes(identity?.ApiKeyId);
                if (maximumOutputBytes is { } maximum && output.Length > maximum)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                    return ApiLimitResponses.TooLarge(context, "output_bytes_exceeded", $"The generated output is {output.Length} bytes; this key allows {maximum} bytes. Retrying will not help unless the output is smaller.", "maximum output bytes", maximum);
                }
                lease?.CommitSuccess(file.Length, output.Length, timer.Elapsed.TotalMilliseconds, output.Metrics?.CpuMilliseconds ?? 0, output.Metrics?.PeakProcessTreeMemoryBytes ?? 0);
                return new ConversionFileResult(output);
            }
            finally { lease?.Dispose(); }
        })
            .RequireScope(ApiKeyScopes.ConvertSubmit)
            .DisableAntiforgery();

        app.MapPost("/v1/create-text-pdf", async (HttpContext context, TextPdfRequest request, string? watermarkProfile, IsolatedConversionService service, ApiAdmissionService admission, WatermarkProfileStore watermarkProfiles, CancellationToken cancellationToken) =>
        {
            var resolvedWatermark = ConversionApiHelpers.ResolveWatermark(watermarkProfile, request.Watermark, watermarkProfiles, out var watermarkError);
            if (watermarkError is not null) return watermarkError;
            var identity = ApiAuthentication.GetIdentity(context);
            var inputBytes = System.Text.Encoding.UTF8.GetByteCount(request.Text ?? string.Empty);
            var failure = admission.TryAdmit(identity, new ApiAdmissionRequest(context.Request.ContentLength ?? inputBytes, inputBytes, inputBytes, 1, Queued: false, Format: "txt"), out var lease);
            if (failure is not null) return failure.ToResult(context);
            try
            {
                var timer = Stopwatch.StartNew();
                var output = await service.CreateTextPdfAsync(request.Text ?? string.Empty, request.Profile ?? DefaultProfile(), watermark: resolvedWatermark, cancellationToken: cancellationToken).ConfigureAwait(false);
                var maximumOutputBytes = admission.GetMaximumOutputBytes(identity?.ApiKeyId);
                if (maximumOutputBytes is { } maximum && output.Length > maximum)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                    return ApiLimitResponses.TooLarge(context, "output_bytes_exceeded", $"The generated output is {output.Length} bytes; this key allows {maximum} bytes. Retrying will not help unless the output is smaller.", "maximum output bytes", maximum);
                }
                lease?.CommitSuccess(inputBytes, output.Length, timer.Elapsed.TotalMilliseconds, output.Metrics?.CpuMilliseconds ?? 0, output.Metrics?.PeakProcessTreeMemoryBytes ?? 0);
                return new ConversionFileResult(output);
            }
            finally { lease?.Dispose(); }
        }).RequireScope(ApiKeyScopes.ConvertSubmit);

        app.MapPost("/v1/convert-and-merge", async (HttpContext context, IFormFileCollection files, string? profile, string? watermarkProfile, string? watermark, IsolatedConversionService service, ApiRequestLimits limits, ApiAdmissionService admission, WatermarkProfileStore watermarkProfiles, CancellationToken cancellationToken) =>
        {
            if (files.Count > limits.MaximumFiles)
                return ApiLimitResponses.TooLarge(context, "file_count_exceeded", $"A request cannot contain more than {limits.MaximumFiles} files.", "maximum files per request", limits.MaximumFiles);
            if (files.Any(file => file.Length > limits.MaximumFileBytes))
                return ApiLimitResponses.TooLarge(context, "file_bytes_exceeded", $"Each file must be at most {limits.MaximumFileBytes} bytes.", "maximum file bytes", limits.MaximumFileBytes);
            WatermarkOptions? directWatermark;
            try { directWatermark = ConversionApiHelpers.ParseWatermark(watermark); }
            catch (JsonException) { return Results.BadRequest(new { code = "invalid_watermark", message = "The watermark JSON is invalid." }); }
            var resolvedWatermark = ConversionApiHelpers.ResolveWatermark(watermarkProfile, directWatermark, watermarkProfiles, out var watermarkError);
            if (watermarkError is not null) return watermarkError;
            var identity = ApiAuthentication.GetIdentity(context);
            var inputBytes = files.Sum(file => file.Length);
            var failure = admission.TryAdmit(identity, new ApiAdmissionRequest(context.Request.ContentLength ?? inputBytes, inputBytes, files.Count == 0 ? 0 : files.Max(file => file.Length), files.Count, Queued: false, Format: "mixed"), out var lease);
            if (failure is not null) return failure.ToResult(context);
            var inputs = files.Select(file => (file.OpenReadStream(), file.FileName, file.ContentType)).ToArray();
            try
            {
                var timer = Stopwatch.StartNew();
                var output = await service.ConvertAndMergeAsync(inputs, profile ?? DefaultProfile(), watermark: resolvedWatermark, cancellationToken: cancellationToken).ConfigureAwait(false);
                var maximumOutputBytes = admission.GetMaximumOutputBytes(identity?.ApiKeyId);
                if (maximumOutputBytes is { } maximum && output.Length > maximum)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                    return ApiLimitResponses.TooLarge(context, "output_bytes_exceeded", $"The generated output is {output.Length} bytes; this key allows {maximum} bytes. Retrying will not help unless the output is smaller.", "maximum output bytes", maximum);
                }
                lease?.CommitSuccess(inputBytes, output.Length, timer.Elapsed.TotalMilliseconds, output.Metrics?.CpuMilliseconds ?? 0, output.Metrics?.PeakProcessTreeMemoryBytes ?? 0);
                return new ConversionFileResult(output);
            }
            finally { foreach (var input in inputs) input.Item1.Dispose(); lease?.Dispose(); }
        }).RequireScope(ApiKeyScopes.ConvertSubmit).DisableAntiforgery();
    }

    private sealed record TextPdfRequest(string? Text, string? Profile, InactivePDF.Domain.Models.WatermarkOptions? Watermark);

    private static string DefaultProfile() => Environment.GetEnvironmentVariable("INACTIVEPDF_DEFAULT_PROFILE") ?? "archive";
}

internal static class ApiLimitResponses
{
    public static IResult TooLarge(HttpContext context, string code, string message, string limit, object configuredLimit)
    {
        context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
        return Results.Json(new { code, message, limit, configuredLimit = Convert.ToString(configuredLimit, System.Globalization.CultureInfo.InvariantCulture), retryable = false, requestId = context.TraceIdentifier }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
}
