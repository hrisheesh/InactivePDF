using InactivePDF.Application.Watermarks;
using InactivePDF.Domain.Models;

namespace InactivePDF.Api;

public sealed record ProfileWriteRequest(
    string? Name,
    string? Type,
    WatermarkOptions? Options);

internal static class ProfileAdministration
{
    public static IResult Create(ProfileWriteRequest request, WatermarkProfileStore store)
    {
        if (!IsWatermark(request.Type, out var typeError)) return typeError!;
        if (!WatermarkProfileStore.IsValidName(request.Name ?? string.Empty))
            return Results.BadRequest(new { code = "invalid_profile_name", message = "Profile names must be a single safe filename component." });
        if (request.Options is null) return Results.BadRequest(new { code = "profile_options_required", message = "Watermark profile options are required." });
        var validation = WatermarkProfileValidator.Validate(request.Options);
        if (validation.Count > 0) return Validation(validation);
        if (store.TryGet(request.Name!, out _))
            return Results.Conflict(new { code = "profile_exists", message = $"The watermark profile '{request.Name}' already exists." });

        store.Save(request.Name!, request.Options);
        return Results.Created($"/v1/admin/profiles/{Uri.EscapeDataString(request.Name!)}", ProfileApiResponses.Lookup(request.Name!, store));
    }

    public static IResult Update(string name, ProfileWriteRequest request, WatermarkProfileStore store)
    {
        if (!IsWatermark(request.Type, out var typeError)) return typeError!;
        if (!WatermarkProfileStore.IsValidName(name))
            return Results.BadRequest(new { code = "invalid_profile_name", message = "Profile names must be a single safe filename component." });
        if (request.Options is null) return Results.BadRequest(new { code = "profile_options_required", message = "Watermark profile options are required." });
        var validation = WatermarkProfileValidator.Validate(request.Options);
        if (validation.Count > 0) return Validation(validation);

        store.Save(name, request.Options);
        return Results.Ok(ProfileApiResponses.Lookup(name, store));
    }

    public static IResult Delete(string name, string? type, WatermarkProfileStore store)
    {
        if (!IsWatermark(type, out var typeError)) return typeError!;
        if (!WatermarkProfileStore.IsValidName(name))
            return Results.BadRequest(new { code = "invalid_profile_name", message = "Profile names must be a single safe filename component." });
        return store.Delete(name) ? Results.NoContent() : Results.NotFound();
    }

    private static bool IsWatermark(string? type, out IResult? error)
    {
        if (string.IsNullOrWhiteSpace(type) || string.Equals(type, "watermark", StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        error = Results.BadRequest(new
        {
            code = "unsupported_profile_type",
            message = "Only watermark profiles are managed by these routes. PDF output profiles are configured under Conversion.Profiles in InactivePDF.settings.json."
        });
        return false;
    }

    private static IResult Validation(IReadOnlyList<string> errors) => Results.ValidationProblem(
        errors.Select((error, index) => new KeyValuePair<string, string[]>(index.ToString(System.Globalization.CultureInfo.InvariantCulture), [error])).ToDictionary());
}
