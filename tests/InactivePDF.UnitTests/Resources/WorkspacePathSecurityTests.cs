using InactivePDF.Infrastructure.Resources;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.IO;

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

    [Fact]
    public async Task RejectsOutputSymlinkWithoutOverwritingTheTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        var link = Path.Combine(root, "output.pdf");
        try
        {
            await File.WriteAllTextAsync(outside, "protected");
            try { File.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }

            var publisher = new AtomicFilePublisher(new BoundedStreamCopier());
            await using var source = new MemoryStream(new byte[] { 1, 2, 3 });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => publisher.PublishAsync(source, link, 100));
            Assert.Equal("protected", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            try { File.Delete(link); } catch (IOException) { }
            try { File.Delete(outside); } catch (IOException) { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RejectsWatermarkAssetSymlinkAndAbsolutePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"), "logo.png");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        var link = Path.Combine(root, "logo.png");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => WatermarkAssetResolver.Resolve(
                new WatermarkOptions(Kind: WatermarkKind.Image, ImagePath: outside), root));
            File.WriteAllBytes(outside, [1, 2, 3]);
            try { File.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }
            Assert.Throws<UnauthorizedAccessException>(() => WatermarkAssetResolver.Resolve(
                new WatermarkOptions(Kind: WatermarkKind.Image, ImagePath: "logo.png"), root));
        }
        finally
        {
            try { File.Delete(link); } catch (IOException) { }
            try { File.Delete(outside); } catch (IOException) { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
            try { var parent = Path.GetDirectoryName(outside); if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, true); } catch (IOException) { }
        }
    }
}
