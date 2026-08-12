using InactivePDF.Infrastructure.Configuration;

namespace InactivePDF.UnitTests.Configuration;

public sealed class InactivePdfSettingsTests
{
    [Fact]
    public void RepositorySettingsFileLoadsTheCurrentDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "InactivePDF.settings.json");
        var settings = InactivePdfSettings.Load(path);

        Assert.Equal(512L * 1024 * 1024, settings.Api.MaximumRequestBytes);
        Assert.Equal(256L * 1024 * 1024, settings.Api.MaximumFileBytes);
        Assert.Equal(50_000_000, settings.Resources.MaximumImagePixels);
        Assert.Equal(1_610_612_736, settings.Workers.MaximumMemoryBytes);
        Assert.Equal(2, settings.WatchFolder.ScanIntervalSeconds);
        Assert.Equal(2, settings.WatchFolder.FileStabilityDelaySeconds);
        Assert.Equal("archive", settings.Conversion.DefaultProfile);
        Assert.Equal("ConvertFile", settings.Conversion.DefaultOperation);
        Assert.Equal(17, settings.Conversion.Profiles.Archive.PdfVersion);
        Assert.True(settings.Conversion.Profiles.Archive.PreserveJpegData);
        Assert.Equal(150, settings.Conversion.Profiles.Compact.MaximumImageDpi);
        Assert.Equal("visual-layout", settings.Conversion.Profiles.Compatibility.LargeMarkupPolicy);
    }
}
