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
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
