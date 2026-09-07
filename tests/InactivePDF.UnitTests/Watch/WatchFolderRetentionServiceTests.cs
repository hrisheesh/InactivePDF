using InactivePDF.Infrastructure.Watch;

namespace InactivePDF.UnitTests.Watch;

public sealed class WatchFolderRetentionServiceTests
{
    [Fact]
    public void SizeLimitDeletesOldestEligibleOutputAndLeavesRecentOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folders = new WatchFolderOptions(root);
            WatchFolderLayout.Ensure(folders);
            var oldPath = Path.Combine(folders.OutputPath, "old.pdf");
            var recentPath = Path.Combine(folders.OutputPath, "recent.pdf");
            File.WriteAllBytes(oldPath, new byte[80]);
            File.WriteAllBytes(recentPath, new byte[80]);

            var now = DateTimeOffset.UtcNow;
            File.SetLastWriteTimeUtc(oldPath, now.UtcDateTime.AddMinutes(-10));
            File.SetLastWriteTimeUtc(recentPath, now.UtcDateTime.AddSeconds(-1));

            var retention = new WatchFolderRetentionOptions(
                Enabled: true,
                MaximumOutputBytes: 100,
                MinimumFileAgeSeconds: 60,
                DeleteOutputFiles: true);
            var result = new WatchFolderRetentionService(folders, retention).Sweep(now);

            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(80, result.DeletedBytes);
            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(recentPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DisabledRetentionDoesNotDeleteFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folders = new WatchFolderOptions(root);
            WatchFolderLayout.Ensure(folders);
            var output = Path.Combine(folders.OutputPath, "output.pdf");
            File.WriteAllBytes(output, new byte[10]);

            var result = new WatchFolderRetentionService(folders, new WatchFolderRetentionOptions()).Sweep(DateTimeOffset.UtcNow);

            Assert.False(result.Enabled);
            Assert.Equal(0, result.DeletedFiles);
            Assert.True(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
