namespace InactivePDF.Infrastructure.Processes;

public sealed class LibreOfficeSessionLease(string profilePath, int processId, Func<ValueTask> release) : IAsyncDisposable
{
    private int _disposed;
    private readonly Func<ValueTask> _release = release ?? throw new ArgumentNullException(nameof(release));

    public string ProfilePath { get; } = profilePath;
    public int ProcessId { get; } = processId;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? _release() : ValueTask.CompletedTask;
}
