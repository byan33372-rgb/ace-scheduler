using System.Management;

namespace AceScheduler;

/// <summary>
/// Cumulative read-bytes sampler for one PID (Win32_Process.ReadTransferCount).
/// Sampling cost is real (~1s under load): all state-machine timing must use
/// wall-clock deltas from sample timestamps, never tick counts - see docs/design.md.
/// </summary>
public static class IoSampler
{
    public static double? GetCumulativeReadBytes(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ReadTransferCount FROM Win32_Process WHERE ProcessId = " + processId);
            foreach (var o in searcher.Get())
            {
                return Convert.ToDouble(o["ReadTransferCount"]);
            }
        }
        catch
        {
            // process gone or WMI hiccup - caller treats null as "skip tick"
        }
        return null;
    }
}
