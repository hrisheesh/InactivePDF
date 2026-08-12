namespace InactivePDF.Domain.Contracts;

public interface IProcessTreeController
{
    Task WaitForExitAsync(ProcessHandle process, TimeSpan timeout, CancellationToken cancellationToken = default);
    void Terminate(ProcessHandle process);
}

public sealed record ProcessHandle(int ProcessId);
