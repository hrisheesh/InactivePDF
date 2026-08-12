using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;

namespace InactivePDF.UnitTests.Jobs;

public sealed class ConversionConcurrencyGateTests
{
    [Fact]
    public async Task OfficeGateBlocksSecondOfficeJobUntilFirstLeaseIsReleased()
    {
        using var gate = new ConversionConcurrencyGate(new ConversionConcurrencyOptions(
            OfficeConcurrency: 1,
            ImageConcurrency: 1,
            PdfConcurrency: 1,
            TextConcurrency: 2));
        var first = CreateWorkItem("first.docx");
        var second = CreateWorkItem("second.docx");
        await using var firstLease = await gate.AcquireAsync(first);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await gate.AcquireAsync(second, cancellation.Token));

        await firstLease.DisposeAsync();
        await using var secondLease = await gate.AcquireAsync(second);
    }

    private static ConversionWorkItem CreateWorkItem(string fileName) => new(
        new ConversionJob(Guid.NewGuid(), fileName, ConversionOperation.ConvertFile, ConversionJobState.Accepted, DateTimeOffset.UtcNow),
        new ConversionRequest(fileName, ConversionOperation.ConvertFile, [new DocumentInput(fileName, "application/octet-stream", 1)], new ConversionOptions()),
        [new StoredInput(fileName, fileName, 1)],
        Path.GetTempPath());
}
