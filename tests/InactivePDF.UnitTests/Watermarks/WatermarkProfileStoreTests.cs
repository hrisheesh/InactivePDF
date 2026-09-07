using InactivePDF.Api;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.UnitTests.Watermarks;

public sealed class WatermarkProfileStoreTests
{
    [Fact]
    public void SavesAndReloadsProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new WorkspaceOptions { RootPath = root };
            new WatermarkProfileStore(options).Save("draft", new WatermarkOptions(Text: "DRAFT"));
            var reloaded = new WatermarkProfileStore(options);
            Assert.True(reloaded.TryGet("draft", out var profile));
            Assert.Equal("DRAFT", profile.Text);
            Assert.Empty(Directory.EnumerateFiles(root, "watermark-profiles.json.tmp-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData("..")]
    public void RejectsUnsafeProfileNames(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WatermarkProfileStore(new WorkspaceOptions { RootPath = root });
            Assert.Throws<ArgumentException>(() => store.Save(name, new WatermarkOptions(Text: "DRAFT")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
