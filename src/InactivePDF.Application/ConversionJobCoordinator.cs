using InactivePDF.Application.Abstractions;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;

namespace InactivePDF.Application;

public sealed class ConversionJobCoordinator(
    IConversionRequestValidator validator,
    IConversionJobIdGenerator jobIdGenerator,
    IClock clock) : IConversionJobCoordinator
{
    public ConversionJob Accept(ConversionRequest request)
    {
        var errors = validator.Validate(request);
        if (errors.Count > 0)
        {
            throw new ConversionRequestValidationException(errors);
        }

        return new(
            jobIdGenerator.Create(),
            request.CorrelationId,
            request.Operation,
            ConversionJobState.Accepted,
            clock.UtcNow);
    }
}

public sealed class ConversionRequestValidationException(
    IReadOnlyList<ConversionError> errors) : Exception("The conversion request is invalid.")
{
    public IReadOnlyList<ConversionError> Errors { get; } = errors;
}
