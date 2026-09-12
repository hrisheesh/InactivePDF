using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.UnitTests.Processes;

public sealed class CompatibilityConversionServiceTests
{
    [Fact]
    public async Task ConversionPublishesOnlyAfterOutputInspection()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "source.pdf");
        var output = Path.Combine(root, "output.pdf");
        await File.WriteAllTextAsync(input, "%PDF-1.4\nsource");

        try
        {
            var inspector = new TestPdfOperations();
            var service = new CompatibilityConversionService(
                new TestImageConverter(),
                new TestTextGenerator(),
                new TestOfficeConverter(),
                inspector,
                new ResourcePolicy(1024, 1024, 0, TimeSpan.FromMinutes(1)));

            await service.ConvertRequestToFileAsync(new ConversionWorkerRequest(
                ConversionOperation.ConvertFile,
                output,
                [new ConversionWorkerInput(input, "source.pdf")],
                ExecutionMode: ConversionExecutionMode.Development));

            Assert.True(File.Exists(output));
            Assert.Equal("%PDF-1.4\nsource", await File.ReadAllTextAsync(output));
            Assert.Equal(2, inspector.InspectCount);
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial.pdf"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionPassThroughDoesNotInspectTheInputAndOutputTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "source.pdf");
        var output = Path.Combine(root, "output.pdf");
        await File.WriteAllTextAsync(input, "%PDF-1.4\nsource");

        try
        {
            var inspector = new TestPdfOperations();
            var service = new CompatibilityConversionService(
                new TestImageConverter(),
                new TestTextGenerator(),
                new TestOfficeConverter(),
                inspector,
                new ResourcePolicy(1024, 1024, 0, TimeSpan.FromMinutes(1)));

            await service.ConvertRequestToFileAsync(new ConversionWorkerRequest(
                ConversionOperation.ConvertFile,
                output,
                [new ConversionWorkerInput(input, "source.pdf")],
                ExecutionMode: ConversionExecutionMode.Production));

            Assert.Equal(1, inspector.InspectCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OutputLimitStopsPublicationAndRemovesPartialFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "body.txt");
        var output = Path.Combine(root, "output.pdf");
        await File.WriteAllTextAsync(input, "text");

        try
        {
            var service = new CompatibilityConversionService(
                new TestImageConverter(),
                new TestTextGenerator { BytesToWrite = 32 },
                new TestOfficeConverter(),
                new TestPdfOperations(),
                new ResourcePolicy(1024, 4, 0, TimeSpan.FromMinutes(1)));

            await Assert.ThrowsAsync<IOException>(() => service.ConvertRequestToFileAsync(new ConversionWorkerRequest(
                ConversionOperation.CreateTextPdf,
                output,
                [new ConversionWorkerInput(input, "body.txt")] )));

            Assert.False(File.Exists(output));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial.pdf"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DevelopmentRecordsDetailedStagesButProductionDoesNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "body.txt");
        await File.WriteAllTextAsync(input, "text");

        try
        {
            var developmentRecorder = new ConversionStageRecorder();
            var developmentService = CreateTextService(developmentRecorder);
            await developmentService.ConvertRequestToFileAsync(new ConversionWorkerRequest(
                ConversionOperation.CreateTextPdf,
                Path.Combine(root, "development.pdf"),
                [new ConversionWorkerInput(input, "body.txt", "text/plain")],
                ExecutionMode: ConversionExecutionMode.Development));

            var productionRecorder = new ConversionStageRecorder();
            var productionService = CreateTextService(productionRecorder);
            await productionService.ConvertRequestToFileAsync(new ConversionWorkerRequest(
                ConversionOperation.CreateTextPdf,
                Path.Combine(root, "production.pdf"),
                [new ConversionWorkerInput(input, "body.txt", "text/plain")],
                ExecutionMode: ConversionExecutionMode.Production));

            Assert.Contains("conversion", developmentRecorder.Snapshot().Keys);
            Assert.Contains("requiredValidation", developmentRecorder.Snapshot().Keys);
            Assert.Empty(productionRecorder.Snapshot());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CompatibilityConversionService CreateTextService(ConversionStageRecorder recorder) => new(
        new TestImageConverter(),
        new TestTextGenerator(),
        new TestOfficeConverter(),
        new TestPdfOperations(),
        new ResourcePolicy(1024, 1024, 0, TimeSpan.FromMinutes(1)),
        stageRecorder: recorder);

    private sealed class TestImageConverter : IImageToPdfConverter
    {
        public ImageConversionResult Convert(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null) =>
            throw new NotSupportedException();
    }

    private sealed class TestTextGenerator : ITextPdfGenerator
    {
        public int BytesToWrite { get; init; } = 6;

        public TextPdfResult Create(string text, string outputPath, TextPdfOptions? options = null)
        {
            File.WriteAllBytes(outputPath, Enumerable.Repeat((byte)'x', BytesToWrite).ToArray());
            return new TextPdfResult(outputPath, 1, BytesToWrite);
        }
    }

    private sealed class TestOfficeConverter : IOfficeDocumentConverter
    {
        public Task<OfficeConversionResult> ConvertAsync(string inputPath, string outputPath, PdfOutputProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestPdfOperations : IPdfOperations
    {
        public int InspectCount { get; private set; }

        public PdfInspection Inspect(string filePath)
        {
            InspectCount++;
            return new PdfInspection(filePath, new FileInfo(filePath).Length, 1, false);
        }

        public void Merge(IReadOnlyList<string> inputPaths, string outputPath, PdfOutputProfile? profile = null) =>
            File.Copy(inputPaths[0], outputPath, overwrite: true);

        public void ApplyProfile(string filePath, PdfOutputProfile profile) { }
    }
}
