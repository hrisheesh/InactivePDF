using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.UnitTests.Processes;

public sealed class LibreOfficeConverterTests
{
    [Fact]
    public async Task ConvertRejectsUnsupportedExtensionsBeforeStartingProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.bin");
        await File.WriteAllTextAsync(input, "not an office document");
        try
        {
            var converter = new LibreOfficeConverter(new LibreOfficeOptions("missing-soffice", TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<NotSupportedException>(() => converter.ConvertAsync(input, Path.Combine(root, "output.pdf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertReportsMissingLibreOfficeExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.txt");
        await File.WriteAllTextAsync(input, "text");
        try
        {
            var converter = new LibreOfficeConverter(new LibreOfficeOptions("missing-soffice", TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<FileNotFoundException>(() => converter.ConvertAsync(input, Path.Combine(root, "output.pdf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
