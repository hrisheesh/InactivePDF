using InactivePDF.Application.Capabilities;

namespace InactivePDF.UnitTests.Capabilities;

public sealed class PdfOutputProfileCatalogTests
{
    [Fact]
    public void ProfilesAreExplicitAndMeaningfullyDifferent()
    {
        var archive = PdfOutputProfileCatalog.Resolve("archive");
        var compact = PdfOutputProfileCatalog.Resolve("compact");
        var compatibility = PdfOutputProfileCatalog.Resolve("compatibility");

        Assert.Equal(17, archive.PdfVersion);
        Assert.True(archive.PreserveJpegData);
        Assert.False(archive.DownsampleImages);
        Assert.True(compact.DownsampleImages);
        Assert.Equal(150, compact.MaximumImageDpi);
        Assert.Equal(75, compact.JpegQuality);
        Assert.Equal(14, compatibility.PdfVersion);
        Assert.Equal("none", compatibility.EncryptionPolicy);
        Assert.NotEqual(archive, compact);
        Assert.NotEqual(archive, compatibility);
    }

    [Fact]
    public void UnknownProfileHasStructuredReason()
    {
        var exception = Assert.Throws<ConversionFormatException>(() => PdfOutputProfileCatalog.Resolve("unknown"));

        Assert.Equal("unsupported_profile", exception.Code);
    }
}
