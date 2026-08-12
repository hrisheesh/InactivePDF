namespace InactivePDF.Infrastructure.Jobs;

public sealed record ConversionWorkerOptions(
    int WorkerCount = 1,
    int MaximumAttempts = 2,
    int QueueCapacity = 256,
    TimeSpan WorkerTimeout = default,
    long MaximumWorkerWorkingSetBytes = 1_610_612_736,
    TimeSpan JobLeaseDuration = default,
    TimeSpan DispatcherPollInterval = default,
    TimeSpan RetryBaseDelay = default)
{
    public static ConversionWorkerOptions FromEnvironment() => new(
        Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_WORKER_COUNT"), out var count) ? count : 1),
        Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_WORKER_MAX_ATTEMPTS"), out var attempts) ? attempts : 2),
        Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_QUEUE_CAPACITY"), out var capacity) ? capacity : 256),
        TimeSpan.FromSeconds(Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_CONVERSION_WORKER_TIMEOUT_SECONDS"), out var seconds) ? seconds : 240)),
        ParseBytes("INACTIVEPDF_WORKER_MAX_MEMORY_BYTES", 1_610_612_736),
        TimeSpan.FromSeconds(Math.Max(30, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_JOB_LEASE_SECONDS"), out var leaseSeconds) ? leaseSeconds : 300)),
        TimeSpan.FromMilliseconds(Math.Max(25, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_DISPATCHER_POLL_MILLISECONDS"), out var pollMilliseconds) ? pollMilliseconds : 250)),
        TimeSpan.FromMilliseconds(Math.Max(25, int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_RETRY_BASE_DELAY_MILLISECONDS"), out var retryMilliseconds) ? retryMilliseconds : 250)));

    public TimeSpan EffectiveWorkerTimeout => WorkerTimeout == default ? TimeSpan.FromMinutes(4) : WorkerTimeout;
    public TimeSpan EffectiveJobLeaseDuration => JobLeaseDuration == default ? TimeSpan.FromMinutes(5) : JobLeaseDuration;
    public TimeSpan EffectiveDispatcherPollInterval => DispatcherPollInterval == default ? TimeSpan.FromMilliseconds(250) : DispatcherPollInterval;
    public TimeSpan EffectiveRetryBaseDelay => RetryBaseDelay == default ? TimeSpan.FromMilliseconds(250) : RetryBaseDelay;

    private static long ParseBytes(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
