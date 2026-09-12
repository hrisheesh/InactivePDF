using InactivePDF.Infrastructure.Watch;

namespace InactivePDF.UnitTests.Watch;

public sealed class WatchFolderLoggerSecurityTests
{
    [Fact]
    public async Task ErrorLogsDoNotPersistExceptionDiagnosticsOrPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var logger = new WatchFolderLogger(root))
            {
                await logger.WriteAsync(
                    "error",
                    "private.docx",
                    success: false,
                    attempt: 1,
                    exception: new IOException("secret engine stderr /Users/private/document.docx"));
            }

            var contents = string.Join('\n', Directory.EnumerateFiles(root).Select(File.ReadAllText));
            Assert.DoesNotContain("secret engine stderr", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("/Users/private", contents, StringComparison.Ordinal);
            Assert.Contains("io_error", contents, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
