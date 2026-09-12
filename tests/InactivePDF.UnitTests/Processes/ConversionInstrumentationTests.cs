using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.UnitTests.Processes;

public sealed class ConversionInstrumentationTests
{
    [Fact]
    public void StageRecorderCapturesWallAndCpuTime()
    {
        var recorder = new ConversionStageRecorder();

        using (recorder.Measure("inputLoad"))
            Thread.SpinWait(10_000);

        var timing = recorder.Snapshot()["inputLoad"];
        Assert.True(timing.WallMilliseconds >= 0);
        Assert.True(timing.CpuMilliseconds >= 0);
    }

    [Fact]
    public async Task TelemetryPersistsStageAndProcessTreeMetrics()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-instrumentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(input, "sample");
        try
        {
            using var store = new ConversionTelemetryStore(Path.Combine(root, "analytics.db"));
            var observation = store.Begin(new ConversionWorkerRequest(
                ConversionOperation.ConvertFile,
                Path.Combine(root, "output.pdf"),
                [new ConversionWorkerInput(input, "sample.txt", "text/plain")]));
            Assert.NotNull(observation);
            var stages = new ConversionStageReport(new Dictionary<string, ConversionStageTiming>
            {
                ["conversion"] = new(12.5, 8.5)
            });
            store.Complete(observation, 15, Path.Combine(root, "output.pdf"), null, new IsolatedWorkerMetrics(42, 100, 120, 4, 9, 180, 9, 3, stages));

            var row = Assert.Single(store.Search("sample.txt"));
            Assert.Equal("Production", row.ExecutionMode);
            Assert.Equal(3, row.PeakProcessCount);
            Assert.Equal(180, row.PeakProcessTreeMemoryBytes);
            Assert.Equal(9, row.ProcessTreeCpuMilliseconds);
            Assert.Equal(12.5, row.Stages!["conversion"].WallMilliseconds);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
