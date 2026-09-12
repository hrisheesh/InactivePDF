using System.Diagnostics;

namespace InactivePDF.Infrastructure.Processes;

public sealed record ConversionStageTiming(double WallMilliseconds, double CpuMilliseconds);

public sealed record ConversionStageReport(IReadOnlyDictionary<string, ConversionStageTiming> Stages);

public sealed class ConversionStageRecorder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConversionStageTiming> _stages = new(StringComparer.Ordinal);

    public IDisposable Measure(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Measurement(this, name);
    }

    public IReadOnlyDictionary<string, ConversionStageTiming> Snapshot()
    {
        lock (_gate) return new Dictionary<string, ConversionStageTiming>(_stages, StringComparer.Ordinal);
    }

    private void Add(string name, double wallMilliseconds, double cpuMilliseconds)
    {
        lock (_gate)
        {
            if (_stages.TryGetValue(name, out var previous))
            {
                wallMilliseconds += previous.WallMilliseconds;
                cpuMilliseconds += previous.CpuMilliseconds;
            }
            _stages[name] = new(Math.Max(0, wallMilliseconds), Math.Max(0, cpuMilliseconds));
        }
    }

    private sealed class Measurement(ConversionStageRecorder recorder, string name) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TimeSpan _cpuStarted = CurrentCpuTime();
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            recorder.Add(name, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, Math.Max(0, (CurrentCpuTime() - _cpuStarted).TotalMilliseconds));
        }

        private static TimeSpan CurrentCpuTime()
        {
            try { return Process.GetCurrentProcess().TotalProcessorTime; }
            catch (InvalidOperationException) { return TimeSpan.Zero; }
        }
    }
}
