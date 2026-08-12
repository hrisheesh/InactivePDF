namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// A streamed conversion result whose temporary workspace is cleaned after the response closes.
/// </summary>
public sealed class IsolatedConversionOutput(
    FileStream content,
    long length,
    string downloadName,
    IAsyncDisposable workspace) : IAsyncDisposable
{
    private int _disposed;

    public FileStream Content { get; } = content ?? throw new ArgumentNullException(nameof(content));
    public long Length { get; } = length;
    public string DownloadName { get; } = string.IsNullOrWhiteSpace(downloadName) ? "converted.pdf" : downloadName;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await Content.DisposeAsync().ConfigureAwait(false);
        await workspace.DisposeAsync().ConfigureAwait(false);
    }
}
