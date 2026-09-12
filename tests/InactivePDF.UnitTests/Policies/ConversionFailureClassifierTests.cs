using InactivePDF.Application.Policies;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests.Policies;

public sealed class ConversionFailureClassifierTests
{
    [Theory]
    [InlineData("unsupported_format", false)]
    [InlineData("access_denied", false)]
    [InlineData("input_missing", false)]
    public void DeterministicInputFailuresArePermanent(string expectedCode, bool expectedRetryable)
    {
        Exception exception = expectedCode switch
        {
            "unsupported_format" => new NotSupportedException("format"),
            "access_denied" => new UnauthorizedAccessException("access"),
            _ => new FileNotFoundException("missing")
        };

        var result = ConversionFailureClassifier.Classify(exception);

        Assert.Equal(expectedCode == "unsupported_format" ? "invalid_input" : expectedCode, result.Code);
        Assert.Equal(expectedRetryable, result.IsRetryable);
    }

    [Fact]
    public void RetryableWorkerFailureKeepsRetryDecision()
    {
        var retryable = ConversionFailureClassifier.Classify(new ConversionWorkerExecutionException("engine unavailable", true));
        var permanent = ConversionFailureClassifier.Classify(new ConversionWorkerExecutionException("bad input", false));

        Assert.True(retryable.IsRetryable);
        Assert.False(permanent.IsRetryable);
        Assert.Equal("worker_execution_failed", retryable.Code);
        Assert.Equal("worker_execution_failed", permanent.Code);
    }

    [Fact]
    public void FailureMessagesNeverExposeEngineDiagnosticsOrPaths()
    {
        var result = ConversionFailureClassifier.Classify(new ConversionWorkerExecutionException(
            "StandardError=secret engine diagnostic /Users/private/document.docx", true));

        Assert.Equal("worker_execution_failed", result.Code);
        Assert.DoesNotContain("secret engine diagnostic", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/private", result.Message, StringComparison.Ordinal);
    }
}
