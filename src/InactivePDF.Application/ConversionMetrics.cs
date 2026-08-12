using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace InactivePDF.Application;

public sealed class ConversionMetrics : IDisposable
{
    private readonly Meter _meter = new("InactivePDF.Conversion");
    public ActivitySource ActivitySource { get; } = new("InactivePDF.Conversion");
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _succeeded;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _retried;
    private readonly Counter<long> _deadLettered;
    private long _acceptedValue;
    private long _succeededValue;
    private long _failedValue;
    private long _retriedValue;
    private long _deadLetteredValue;

    public ConversionMetrics()
    {
        _accepted = _meter.CreateCounter<long>("inactivepdf.jobs.accepted");
        _succeeded = _meter.CreateCounter<long>("inactivepdf.jobs.succeeded");
        _failed = _meter.CreateCounter<long>("inactivepdf.jobs.failed");
        _retried = _meter.CreateCounter<long>("inactivepdf.jobs.retried");
        _deadLettered = _meter.CreateCounter<long>("inactivepdf.jobs.dead_lettered");
    }

    public long Accepted => Interlocked.Read(ref _acceptedValue);
    public long Succeeded => Interlocked.Read(ref _succeededValue);
    public long Failed => Interlocked.Read(ref _failedValue);
    public long Retried => Interlocked.Read(ref _retriedValue);
    public long DeadLettered => Interlocked.Read(ref _deadLetteredValue);
    public void RecordAccepted() { Interlocked.Increment(ref _acceptedValue); _accepted.Add(1); }
    public void RecordSucceeded() { Interlocked.Increment(ref _succeededValue); _succeeded.Add(1); }
    public void RecordFailed() { Interlocked.Increment(ref _failedValue); _failed.Add(1); }
    public void RecordRetried() { Interlocked.Increment(ref _retriedValue); _retried.Add(1); }
    public void RecordDeadLettered() { Interlocked.Increment(ref _deadLetteredValue); _deadLettered.Add(1); }
    public void Dispose() { ActivitySource.Dispose(); _meter.Dispose(); }
}
