namespace InactivePDF.Infrastructure.Jobs;

/// <summary>
/// Weighted admission control for native conversion work. A job reserves memory budget
/// tokens before it starts; unlike a single heavy semaphore, independent image and office
/// jobs can run together when their combined reservation fits the configured budget.
/// </summary>
public sealed class ResourceAdmissionGate : IDisposable
{
    private const long UnitBytes = 64L * 1024 * 1024;
    private readonly SemaphoreSlim _tokens;
    private readonly SemaphoreSlim _concurrency;
    private readonly int _capacity;

    public ResourceAdmissionGate(long budgetBytes, int maximumConcurrentJobs)
    {
        _capacity = checked((int)Math.Max(1, (budgetBytes + UnitBytes - 1) / UnitBytes));
        _tokens = new SemaphoreSlim(_capacity, _capacity);
        _concurrency = new SemaphoreSlim(Math.Max(1, maximumConcurrentJobs), Math.Max(1, maximumConcurrentJobs));
    }

    public async ValueTask<ResourceAdmissionLease> AcquireAsync(long reservationBytes, CancellationToken cancellationToken = default)
    {
        var units = checked((int)Math.Max(1, (reservationBytes + UnitBytes - 1) / UnitBytes));
        if (units > _capacity)
            throw new InvalidOperationException($"The conversion reservation of {reservationBytes} bytes exceeds the resource admission budget.");

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        var acquired = 0;
        try
        {
            for (; acquired < units; acquired++) await _tokens.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ResourceAdmissionLease(_tokens, _concurrency, units);
        }
        catch
        {
            if (acquired > 0) _tokens.Release(acquired);
            _concurrency.Release();
            throw;
        }
    }

    public void Dispose()
    {
        _tokens.Dispose();
        _concurrency.Dispose();
    }
}

public sealed class ResourceAdmissionLease(SemaphoreSlim tokens, SemaphoreSlim concurrency, int units) : IAsyncDisposable
{
    private int _disposed;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            tokens.Release(units);
            concurrency.Release();
        }

        return ValueTask.CompletedTask;
    }
}
