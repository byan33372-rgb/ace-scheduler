using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MvxxcbRestrain;

/// <summary>
/// Periodic housekeeping for this long-running tray process. The .NET runtime
/// keeps freshly-unused heap pages mapped, so a resident tool's working set
/// only ever creeps up; every TrimInterval we force a compacting Gen2
/// collection and empty the working set. Unbounded data structures are pruned
/// at their own sites (goneUntil cooldown entries, SessionMonitor caps).
/// A trim is logged only when it actually reclaimed something meaningful.
/// </summary>
public static class MemoryMaintenance
{
    public static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(5);

    public static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        try
        {
            using var self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
        catch
        {
            // best-effort; failing to shrink the working set is harmless
        }
    }

    public static long WorkingSetMB()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.WorkingSet64 / (1024 * 1024);
        }
        catch
        {
            return -1;
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
