using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Application.Capabilities;

namespace InactivePDF.Application.Validation;

public sealed class ConversionRequestValidator : IConversionRequestValidator
{
    private const int MaximumInputs = 10_000;
    private const int MaximumProfileLength = 64;

    public IReadOnlyList<ConversionError> Validate(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ConversionError>(capacity: 4);

        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            errors.Add(new("invalid_correlation_id", "A correlation ID is required.", false));
        }

        if (request.Inputs is null || request.Inputs.Count == 0)
        {
            errors.Add(new("no_inputs", "At least one input document is required.", false));
        }
        else if (request.Inputs.Count > MaximumInputs)
        {
            errors.Add(new("too_many_inputs", $"A request cannot contain more than {MaximumInputs} inputs.", false));
        }

        if (request.Options is null || string.IsNullOrWhiteSpace(request.Options.Profile))
        {
            errors.Add(new("invalid_profile", "A conversion profile is required.", false));
        }
        else if (request.Options.Profile.Length > MaximumProfileLength)
        {
            errors.Add(new("profile_too_long", $"A profile cannot exceed {MaximumProfileLength} characters.", false));
        }
        else
        {
            try { _ = PdfOutputProfileCatalog.Resolve(request.Options.Profile); }
            catch (ConversionFormatException exception) { errors.Add(new(exception.Code, exception.Message, false)); }
        }

        if (request.Inputs is not null)
        {
            for (var index = 0; index < request.Inputs.Count; index++)
            {
                var input = request.Inputs[index];
                if (string.IsNullOrWhiteSpace(input.FileName))
                {
                    errors.Add(new("invalid_filename", $"Input {index} has no filename.", false));
                }

                if (input.Length < 0)
                {
                    errors.Add(new("invalid_length", $"Input {index} has an invalid length.", false));
                }

                if (!SupportedFormatCatalog.TryGet(Path.GetExtension(input.FileName), out var format))
                {
                    errors.Add(new("unsupported_extension", $"Input {index} has unsupported extension '{Path.GetExtension(input.FileName)}'.", false));
                }
                else
                {
                    var contentType = NormalizeContentType(input.ContentType);
                    if (contentType is not null && contentType is not "application/octet-stream" and not "binary/octet-stream" && !format.ContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
                        errors.Add(new("content_type_mismatch", $"Input {index} content type '{contentType}' does not match '{format.Extension}'.", false));
                }
            }
        }

        return errors;
    }

    private static string? NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf(';');
        return (separator < 0 ? value : value[..separator]).Trim().ToLowerInvariant();
    }
}
