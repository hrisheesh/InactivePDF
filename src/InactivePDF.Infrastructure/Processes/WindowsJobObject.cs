using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Windows process-tree containment for an isolated conversion. Job Objects account for
/// LibreOffice and every other descendant, not only the .NET worker process.
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectBasicAccountingInformation = 1;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private IntPtr _handle;

    private WindowsJobObject(IntPtr handle) => _handle = handle;

    public static WindowsJobObject? Attach(Process process, long maximumMemoryBytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | (maximumMemoryBytes > 0 ? JobObjectLimitJobMemory : 0)
            },
            JobMemoryLimit = maximumMemoryBytes > 0 ? new UIntPtr((ulong)maximumMemoryBytes) : UIntPtr.Zero
        };

        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) ||
            !AssignProcessToJobObject(handle, process.Handle))
        {
            CloseHandle(handle);
            return null;
        }

        return new WindowsJobObject(handle);
    }

    public long PeakJobMemoryBytes
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            var limits = new ExtendedLimitInformation();
            return QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), out _)
                ? checked((long)limits.PeakJobMemoryUsed)
                : 0;
        }
    }

    public long TotalCpuMilliseconds
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            var accounting = new BasicAccountingInformation();
            return QueryBasicInformationJobObject(_handle, JobObjectBasicAccountingInformation, ref accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), out _)
                ? checked((accounting.TotalUserTime + accounting.TotalKernelTime) / TimeSpan.TicksPerMillisecond)
                : 0;
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, uint informationClass, ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, uint informationClass, ref ExtendedLimitInformation information, uint length, out uint returnLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    private static extern bool QueryBasicInformationJobObject(IntPtr job, uint informationClass, ref BasicAccountingInformation information, uint length, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
