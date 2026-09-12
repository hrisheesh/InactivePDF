using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Watch;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests.Processes;

public sealed class ConversionExecutionModeTests
{
    [Fact]
    public void ProductionIsTheConfiguredDefaultAndDevelopmentIsExplicit()
    {
        var settings = new InactivePdfSettings();

        Assert.Equal("Production", settings.Performance.ExecutionMode);
        Assert.Equal(ConversionExecutionMode.Production, ConversionExecutionModeParser.Parse(settings.Performance.ExecutionMode));
        Assert.Equal(ConversionExecutionMode.Development, ConversionExecutionModeParser.Parse("development"));
    }

    [Fact]
    public void InvalidExecutionModeIsRejectedBySettingsValidation()
    {
        var settings = new InactivePdfSettings();
        settings.Performance.ExecutionMode = "trace";

        var exception = Assert.Throws<InvalidDataException>(() => InactivePdfSettings.Validate(settings));

        Assert.Contains("Performance.ExecutionMode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionRequestDefaultsToProductionWithoutDevelopmentTelemetry()
    {
        var request = new ConversionWorkerRequest(
            ConversionOperation.ConvertFile,
            "/tmp/output.pdf",
            [new ConversionWorkerInput("/tmp/input.txt", "input.txt")]);

        Assert.Equal(ConversionExecutionMode.Production, request.ExecutionMode);
        Assert.False(request.ExecutionMode == ConversionExecutionMode.Development);
    }

    [Fact]
    public void WatchFolderOptionsReadTheExecutionModeFromEnvironment()
    {
        const string variable = "INACTIVEPDF_EXECUTION_MODE";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "Development");

            var options = WatchFolderOptions.FromEnvironment();

            Assert.Equal(ConversionExecutionMode.Development, options.ExecutionMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
