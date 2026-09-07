using System.Runtime.InteropServices;

namespace InactivePDF.Infrastructure.Watch;

public readonly record struct ProcessMemoryMetrics(long CurrentBytes, long PeakBytes)
{
    private const int RusageInfoV4 = 4;
    private const int PhysicalFootprintOffset = 72;
    private const int LifetimeMaximumPhysicalFootprintOffset = 240;

    public static ProcessMemoryMetrics Capture(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
                return GetProcessMemoryInfo(process.Handle, out counters, counters.Size)
                    ? new(checked((long)counters.WorkingSetSize), checked((long)counters.PeakWorkingSetSize))
                    : default;
            }
            catch (ArgumentException) { return default; }
            catch (InvalidOperationException) { return default; }
            catch (UnauthorizedAccessException) { return default; }
            catch (System.ComponentModel.Win32Exception) { return default; }
        }

        if (!OperatingSystem.IsMacOS()) return default;

        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            if (ProcPidRusage(processId, RusageInfoV4, buffer) != 0) return default;
            return new(
                Math.Max(0, Marshal.ReadInt64(buffer, PhysicalFootprintOffset)),
                Math.Max(0, Marshal.ReadInt64(buffer, LifetimeMaximumPhysicalFootprintOffset)));
        }
        catch (DllNotFoundException) { return default; }
        catch (EntryPointNotFoundException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage", SetLastError = true)]
    private static extern int ProcPidRusage(int processId, int flavor, IntPtr buffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }
}
