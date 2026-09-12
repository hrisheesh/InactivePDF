using System.Text.Json;
using System.Text.Json.Serialization;
using InactivePDF.Application.Watermarks;
using InactivePDF.Domain.Models;

namespace InactivePDF.Api;

internal static class ConversionApiHelpers
{
    private static readonly JsonSerializerOptions WatermarkJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static WatermarkOptions? ResolveWatermark(
        string? profileName,
        WatermarkOptions? direct,
        WatermarkProfileStore profiles,
        out IResult? error)
    {
        error = null;
        if (direct is not null)
        {
            var directErrors = WatermarkProfileValidator.Validate(direct);
            if (directErrors.Count > 0)
            {
                error = Results.ValidationProblem(directErrors
                    .Select((message, index) => new KeyValuePair<string, string[]>(index.ToString(System.Globalization.CultureInfo.InvariantCulture), [message]))
                    .ToDictionary());
                return null;
            }

            return direct;
        }

        if (string.IsNullOrWhiteSpace(profileName)) return null;
        if (profiles.TryGet(profileName.Trim(), out var selected))
        {
            var profileErrors = WatermarkProfileValidator.Validate(selected);
            if (profileErrors.Count > 0)
            {
                error = Results.ValidationProblem(profileErrors
                    .Select((message, index) => new KeyValuePair<string, string[]>(index.ToString(System.Globalization.CultureInfo.InvariantCulture), [message]))
                    .ToDictionary());
                return null;
            }

            return selected;
        }

        error = Results.NotFound(new
        {
            code = "watermark_profile_not_found",
            message = $"The watermark profile '{profileName.Trim()}' does not exist."
        });
        return null;
    }

    public static WatermarkOptions? ParseWatermark(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return JsonSerializer.Deserialize<WatermarkOptions>(value, WatermarkJsonOptions)
            ?? throw new JsonException("The watermark value must be a JSON object.");
    }

    public static bool IsAsync(string? mode) => string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase);
    public static bool IsSync(string? mode) => string.Equals(mode, "sync", StringComparison.OrdinalIgnoreCase);
}
