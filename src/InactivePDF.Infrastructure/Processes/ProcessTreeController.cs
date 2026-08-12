using InactivePDF.Domain.Contracts;

namespace InactivePDF.Infrastructure.Processes;

public sealed class ProcessTreeController : IProcessTreeController
{
    public async Task WaitForExitAsync(ProcessHandle process, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(timeout);

        try
        {
            using var handle = System.Diagnostics.Process.GetProcessById(process.ProcessId);
            await handle.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Process {process.ProcessId} did not exit within {timeout}.");
        }
    }

    public void Terminate(ProcessHandle process)
    {
        using var handle = System.Diagnostics.Process.GetProcessById(process.ProcessId);
        if (!handle.HasExited)
        {
            handle.Kill(entireProcessTree: true);
        }
    }
}
