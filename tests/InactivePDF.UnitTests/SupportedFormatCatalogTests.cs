using InactivePDF.Application.Capabilities;

namespace InactivePDF.UnitTests;

public sealed class SupportedFormatCatalogTests
{
    [Theory]
    [InlineData(".DOCX")]
    [InlineData(".jpg")]
    [InlineData(".PDF")]
    [InlineData(".csv")]
    [InlineData(".odp")]
    [InlineData(".ODS")]
    public void SupportedExtensionsAreCaseInsensitive(string extension)
    {
        Assert.True(SupportedFormatCatalog.IsSupported(extension));
    }

    [Fact]
    public void UnknownExtensionIsNotSupported()
    {
        Assert.False(SupportedFormatCatalog.IsSupported(".exe"));
    }

    [Fact]
    public void RemovedRoutesAreNotAdvertised()
    {
        Assert.False(SupportedFormatCatalog.IsSupported(".img"));
        Assert.False(SupportedFormatCatalog.IsSupported(".text"));
        Assert.Contains(SupportedFormatCatalog.All, format => format.Extension == ".csv" && format.Route == ConversionFormatRoute.LibreOffice);
        Assert.Contains(SupportedFormatCatalog.All, format => format.Extension == ".odp" && format.Route == ConversionFormatRoute.LibreOffice);
        Assert.Contains(SupportedFormatCatalog.All, format => format.Extension == ".ods" && format.Route == ConversionFormatRoute.LibreOffice);
    }

    [Fact]
    public void CatalogHasUniqueCanonicalExtensionsAndRoutes()
    {
        Assert.Equal(
            SupportedFormatCatalog.All.Count,
            SupportedFormatCatalog.All.Select(format => format.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SupportedFormatCatalog.All, format => Assert.NotEmpty(format.ContentTypes));
    }
}
