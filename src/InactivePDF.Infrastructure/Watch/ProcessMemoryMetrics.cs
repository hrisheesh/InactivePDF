using System.Runtime.InteropServices;

namespace InactivePDF.Infrastructure.Watch;

public readonly record struct ProcessMemoryMetrics(long CurrentBytes, long PeakBytes)
{
    private const int RusageInfoV4 = 4;
    private const int PhysicalFootprintOffset = 72;
    private const int LifetimeMaximumPhysicalFootprintOffset = 240;

    public static ProcessMemoryMetrics Capture(int processId)
    {
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
}
