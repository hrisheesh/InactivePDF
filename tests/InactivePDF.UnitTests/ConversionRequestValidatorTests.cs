using InactivePDF.Application.Validation;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests;

public sealed class ConversionRequestValidatorTests
{
    private readonly ConversionRequestValidator _validator = new();

    [Fact]
    public void ValidRequestHasNoErrors()
    {
        var request = new ConversionRequest(
            "correlation-1",
            ConversionOperation.ConvertFile,
            new[] { new DocumentInput("document.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 42) },
            new ConversionOptions());

        Assert.Empty(_validator.Validate(request));
    }

    [Fact]
    public void EmptyRequestIsRejected()
    {
        var request = new ConversionRequest(
            "",
            ConversionOperation.ConvertFile,
            Array.Empty<DocumentInput>(),
            new ConversionOptions());

        var errors = _validator.Validate(request);

        Assert.Contains(errors, error => error.Code == "invalid_correlation_id");
        Assert.Contains(errors, error => error.Code == "no_inputs");
    }

    [Fact]
    public void NegativeInputLengthIsRejected()
    {
        var request = new ConversionRequest(
            "correlation-1",
            ConversionOperation.ConvertFile,
            new[] { new DocumentInput("document.docx", "application/octet-stream", -1) },
            new ConversionOptions());

        Assert.Contains(_validator.Validate(request), error => error.Code == "invalid_length");
    }
}
