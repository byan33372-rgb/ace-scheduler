using System.Diagnostics;

namespace AceScheduler;

/// <summary>
/// Resource scheduling only: priority class + affinity. This is the same line
/// community tools hold - no injection, no network interference, no access to
/// anti-cheat files. Access-denied on protected processes is harmless.
/// </summary>
public static class ProcessThrottle
{
    /// <summary>Idle + single core when throttled, Normal + all cores when restored.</summary>
    public static bool Apply(Process process, bool throttle, IntPtr fullAffinityMask)
    {
        try
        {
            process.Refresh();
            if (process.HasExited) return false;
            process.PriorityClass = throttle ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
            process.ProcessorAffinity = throttle ? (IntPtr)1 : fullAffinityMask;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>All logical processors as an affinity mask. Fine up to 64 LPs.</summary>
    public static IntPtr FullAffinityMask()
        => (IntPtr)((1L << Environment.ProcessorCount) - 1);
}
