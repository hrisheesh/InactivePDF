using System.Diagnostics;
using InactivePDF.Infrastructure.Watch;

namespace InactivePDF.UnitTests.Watch;

public sealed class ProcessMemoryMetricsTests
{
    [Fact]
    public void CaptureAfterAWorkerExitsIsBestEffortAndDoesNotThrow()
    {
        using var worker = StartShortLivedProcess();
        var processId = worker.Id;
        worker.WaitForExit();

        var capture = ProcessMemoryMetrics.Capture(processId);

        Assert.True(capture.CurrentBytes >= 0);
        Assert.True(capture.PeakBytes >= 0);
    }

    private static Process StartShortLivedProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { UseShellExecute = false }
            : new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        if (OperatingSystem.IsWindows()) startInfo.ArgumentList.Add("/c");
        else startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The short-lived test worker could not start.");
    }
}
