using InactivePDF.Infrastructure.Configuration;

namespace InactivePDF.UnitTests.Configuration;

public sealed class InactivePdfSettingsTests
{
    [Fact]
    public void WatchFolderReadsOutputAndWatermarkProfileSelectionsFromEnvironment()
    {
        var previousOutput = Environment.GetEnvironmentVariable("INACTIVEPDF_WATCH_PROFILE");
        var previousWatermark = Environment.GetEnvironmentVariable("INACTIVEPDF_WATCH_WATERMARK_PROFILE");
        Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_PROFILE", "compact");
        Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_WATERMARK_PROFILE", "internal");
        try
        {
            var options = InactivePDF.Infrastructure.Watch.WatchFolderOptions.FromEnvironment();
            Assert.Equal("compact", options.Profile);
            Assert.Equal("internal", options.WatermarkProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_PROFILE", previousOutput);
            Environment.SetEnvironmentVariable("INACTIVEPDF_WATCH_WATERMARK_PROFILE", previousWatermark);
        }
    }

    [Fact]
    public void RepositorySettingsFileLoadsTheCurrentDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "InactivePDF.settings.json");
        var settings = InactivePdfSettings.Load(path);

        Assert.Equal(512L * 1024 * 1024, settings.Api.MaximumRequestBytes);
        Assert.Equal(256L * 1024 * 1024, settings.Api.MaximumFileBytes);
        Assert.Equal(100_000_000, settings.Resources.MaximumImagePixels);
        Assert.Equal(20_000, settings.Resources.MaximumImageWidth);
        Assert.Equal(256, settings.Resources.MaximumImageFrames);
        Assert.Equal(1_610_612_736, settings.Workers.MaximumMemoryBytes);
        Assert.Equal(2, settings.WatchFolder.ScanIntervalSeconds);
        Assert.Equal(2, settings.WatchFolder.FileStabilityDelaySeconds);
        Assert.False(settings.WatchFolder.Retention.Enabled);
        Assert.Equal(300, settings.WatchFolder.Retention.SweepIntervalSeconds);
        Assert.Equal(0, settings.WatchFolder.Retention.MaximumOutputBytes);
        Assert.Equal(300, settings.WatchFolder.Retention.MinimumFileAgeSeconds);
        Assert.Equal("archive", settings.WatchFolder.Profile);
        Assert.Null(settings.WatchFolder.WatermarkProfile);
        Assert.Equal("archive", settings.Conversion.DefaultProfile);
        Assert.Equal("Production", settings.Performance.ExecutionMode);
        Assert.Equal("ConvertFile", settings.Conversion.DefaultOperation);
        Assert.Equal(17, settings.Conversion.Profiles.Archive.PdfVersion);
        Assert.True(settings.Conversion.Profiles.Archive.PreserveJpegData);
        Assert.Equal(150, settings.Conversion.Profiles.Compact.MaximumImageDpi);
        Assert.Equal("visual-layout", settings.Conversion.Profiles.Compatibility.LargeMarkupPolicy);
    }
}
