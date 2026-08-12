using InactivePDF.Application;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests;

public sealed class ConversionJobCoordinatorTests
{
    [Fact]
    public void AcceptCreatesAnAcceptedJobWithInjectedDependencies()
    {
        var expectedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var expectedTime = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var coordinator = new ConversionJobCoordinator(
            new AlwaysValidRequestValidator(),
            new FixedJobIdGenerator(expectedId),
            new FixedClock(expectedTime));

        var request = new ConversionRequest(
            "correlation-1",
            ConversionOperation.ConvertFile,
            new[] { new DocumentInput("document.docx", "application/octet-stream", 10) },
            new ConversionOptions());

        var job = coordinator.Accept(request);

        Assert.Equal(expectedId, job.Id);
        Assert.Equal(expectedTime, job.AcceptedAt);
        Assert.Equal(ConversionJobState.Accepted, job.State);
        Assert.Equal(request.CorrelationId, job.CorrelationId);
    }

    [Fact]
    public void AcceptRejectsInvalidRequestsBeforeCreatingAJob()
    {
        var coordinator = new ConversionJobCoordinator(
            new InvalidRequestValidator(),
            new ThrowingJobIdGenerator(),
            new FixedClock(DateTimeOffset.UtcNow));

        var request = new ConversionRequest(
            "correlation-1",
            ConversionOperation.ConvertFile,
            Array.Empty<DocumentInput>(),
            new ConversionOptions());

        var exception = Assert.Throws<ConversionRequestValidationException>(() => coordinator.Accept(request));

        Assert.Contains(exception.Errors, error => error.Code == "invalid_test_request");
    }

    private sealed class AlwaysValidRequestValidator : IConversionRequestValidator
    {
        public IReadOnlyList<ConversionError> Validate(ConversionRequest request) => Array.Empty<ConversionError>();
    }

    private sealed class InvalidRequestValidator : IConversionRequestValidator
    {
        public IReadOnlyList<ConversionError> Validate(ConversionRequest request) =>
            new[] { new ConversionError("invalid_test_request", "Invalid for test.", false) };
    }

    private sealed class FixedJobIdGenerator(Guid id) : IConversionJobIdGenerator
    {
        public Guid Create() => id;
    }

    private sealed class ThrowingJobIdGenerator : IConversionJobIdGenerator
    {
        public Guid Create() => throw new InvalidOperationException("The ID generator must not be called.");
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }
}
