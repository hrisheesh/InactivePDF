using InactivePDF.Application.Watermarks;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests.Watermarks;

public sealed class WatermarkProfileValidatorTests
{
    [Fact]
    public void RejectsTextWatermarkWithoutText()
    {
        var errors = WatermarkProfileValidator.Validate(new WatermarkOptions());
        Assert.Contains(errors, error => error.Contains("Text is required", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsValidTextWatermark()
    {
        var errors = WatermarkProfileValidator.Validate(new WatermarkOptions(Text: "CONFIDENTIAL"));
        Assert.Empty(errors);
    }

    [Fact]
    public void RejectsInvalidPageRange()
    {
        var errors = WatermarkProfileValidator.Validate(new WatermarkOptions(Text: "DRAFT", Pages: "4-2"));
        Assert.Contains(errors, error => error.Contains("valid range", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsExplicitPageRange()
    {
        var errors = WatermarkProfileValidator.Validate(new WatermarkOptions(Text: "DRAFT", Pages: "1,3-5"));
        Assert.Empty(errors);
    }

    [Fact]
    public void AcceptsNoPagesSelection()
    {
        var errors = WatermarkProfileValidator.Validate(new WatermarkOptions(Text: "DRAFT", Pages: "none"));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("1-")]
    [InlineData("1,,2")]
    [InlineData("1-abc")]
    public void RejectsMalformedPageRange(string pages)
    {
        Assert.NotEmpty(WatermarkProfileValidator.Validate(new WatermarkOptions(Text: "DRAFT", Pages: pages)));
    }
}
