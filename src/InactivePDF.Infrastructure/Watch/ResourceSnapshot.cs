using System.Diagnostics;

namespace InactivePDF.Infrastructure.Watch;

public sealed record ResourceSnapshot(
    DateTimeOffset Utc,
    int ProcessId,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long VirtualMemoryBytes,
    int ThreadCount,
    long CpuMilliseconds,
    long GcHeapBytes,
    long GcFragmentedBytes,
    long GcTotalMemoryBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int LibreOfficeProcessCount,
    long LibreOfficeWorkingSetBytes,
    int LibreOfficeThreadCount,
    long LibreOfficeCpuMilliseconds,
    long PhysicalFootprintBytes,
    long PhysicalFootprintPeakBytes,
    int ConversionWorkerProcessId,
    long ConversionWorkerPeakWorkingSetBytes,
    long ConversionWorkerPeakPhysicalFootprintBytes,
    int ConversionWorkerPeakThreadCount,
    long ConversionWorkerCpuMilliseconds)
{
    public static ResourceSnapshot Capture()
    {
        using var current = Process.GetCurrentProcess();
        var libreOfficeProcesses = Process.GetProcessesByName("soffice").Concat(Process.GetProcessesByName("soffice.bin")).ToArray();
        try
        {
            var physicalFootprint = ProcessMemoryMetrics.Capture(current.Id);
            return new ResourceSnapshot(
                DateTimeOffset.UtcNow,
                current.Id,
                current.WorkingSet64,
                NormalizePlatformMemory(TryGet(() => current.PrivateMemorySize64)),
                NormalizePlatformMemory(TryGet(() => current.VirtualMemorySize64)),
                current.Threads.Count,
                (long)current.TotalProcessorTime.TotalMilliseconds,
                GC.GetGCMemoryInfo().HeapSizeBytes,
                GC.GetGCMemoryInfo().FragmentedBytes,
                GC.GetTotalMemory(false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                libreOfficeProcesses.Length,
                libreOfficeProcesses.Sum(process => TryGet(() => process.WorkingSet64)),
                (int)libreOfficeProcesses.Sum(process => TryGet(() => process.Threads.Count)),
                libreOfficeProcesses.Sum(process => TryGet(() => (long)process.TotalProcessorTime.TotalMilliseconds)),
                physicalFootprint.CurrentBytes,
                physicalFootprint.PeakBytes,
                0,
                0,
                0,
                0,
                0);
        }
        finally { foreach (var process in libreOfficeProcesses) process.Dispose(); }
    }

    private static long TryGet(Func<long> value) { try { return value(); } catch { return 0; } }
    private static long NormalizePlatformMemory(long value) => OperatingSystem.IsWindows() && value is > 0 and < 1_099_511_627_776 ? value : 0;

    public bool IsMateriallyDifferent(ResourceSnapshot? previous, TimeSpan maximumSilence)
    {
        if (previous is null) return true;
        if (Utc - previous.Utc >= maximumSilence) return true;
        return ChangedByAtLeast(WorkingSetBytes, previous.WorkingSetBytes) ||
            ChangedByAtLeast(PhysicalFootprintBytes, previous.PhysicalFootprintBytes) ||
            ChangedByAtLeast(LibreOfficeWorkingSetBytes, previous.LibreOfficeWorkingSetBytes) ||
            Math.Abs(ThreadCount - previous.ThreadCount) >= 4 ||
            LibreOfficeProcessCount != previous.LibreOfficeProcessCount ||
            Math.Abs(LibreOfficeThreadCount - previous.LibreOfficeThreadCount) >= 4;
    }

    private static bool ChangedByAtLeast(long current, long previous) =>
        current != previous && Math.Abs(current - previous) >= 16L * 1024 * 1024;

    public ResourceSnapshot WithConversionWorker(global::InactivePDF.Infrastructure.Processes.IsolatedWorkerMetrics? worker) => worker is null
        ? this
        : this with
        {
            ConversionWorkerProcessId = worker.ProcessId,
            ConversionWorkerPeakWorkingSetBytes = worker.PeakWorkingSetBytes,
            ConversionWorkerPeakPhysicalFootprintBytes = worker.PeakPhysicalFootprintBytes,
            ConversionWorkerPeakThreadCount = worker.PeakThreadCount,
            ConversionWorkerCpuMilliseconds = worker.CpuMilliseconds
        };
}
