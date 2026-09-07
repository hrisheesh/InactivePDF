using InactivePDF.Infrastructure.Resources;
using InactivePDF.Domain.Models;

namespace InactivePDF.UnitTests.Resources;

public sealed class WorkspacePathSecurityTests
{
    [Fact]
    public void RejectsWorkspaceEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePathSecurity.EnsureSafeChild(root, Path.Combine(root, "..", "outside")));
    }

    [Fact]
    public void RejectsReparsePointInNestedWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        var link = Path.Combine(root, "nested");
        try
        {
            Directory.CreateSymbolicLink(link, target);
            Assert.Throws<UnauthorizedAccessException>(() => WorkspacePathSecurity.EnsureSafeChild(root, Path.Combine(link, "file.pdf")));
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RejectsUnauthorizedWatermarkAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => WatermarkAssetResolver.Resolve(
                new WatermarkOptions(Kind: WatermarkKind.Image, ImagePath: "../secret.png"), root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
