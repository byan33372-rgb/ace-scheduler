using System.Management;
using System.Runtime.InteropServices;

namespace MvxxcbRestrain;

/// <summary>
/// Cumulative read-bytes sampler for one PID.
/// Primary path: kernel32 GetProcessIoCounters - documented, stable, needs
/// only PROCESS_QUERY_LIMITED_INFORMATION, sub-millisecond; keeps the 500ms
/// poll honest. Slow fallback: WMI (SELECT ReadTransferCount FROM
/// Win32_Process), for the rare case the fast handle is denied.
/// All state-machine timing uses wall-clock deltas between samples, never
/// tick counts - see docs/design.md.
/// </summary>
public static class IoSampler
{
    private static int _mode; // 0 = undecided, 1 = win32, 2 = wmi

    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// Sampling verdict for one PID. The fast kernel32 path is ALWAYS probed:
    /// its failure error classifies the target. ERROR_ACCESS_DENIED (5) means
    /// alive but protected (SYSTEM) - the WMI value is authoritative there.
    /// Any other failure (87 = pid gone) returns null immediately: WMI keeps
    /// serving STALE rows for killed processes (observed ~2 minutes in field
    /// tests), which made dead targets immortal and starved sampler-based
    /// liveness.
    /// </summary>
    public static double? GetCumulativeReadBytes(int processId)
    {
        try
        {
            var fast = Win32ReadBytes(processId, out var fastErr);
            if (fast.HasValue)
            {
                _mode = 1;
                return fast.Value;
            }
            if (fastErr == ErrorAccessDenied) return WmiReadBytes(processId);
            return null; // process gone (or sampler broken): never trust stale WMI
        }
        catch
        {
            return null;
        }
    }

    private static double? WmiReadBytes(int processId)
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
            // WMI unavailable or process gone
        }
        return null;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static double? Win32ReadBytes(int processId, out int win32Error)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            win32Error = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            // A force-killed process whose object is still referenced (e.g. a
            // parent holding a Process object) answers OpenProcess and serves
            // FROZEN IO counters forever - rate alone cannot tell "dead" from
            // "idle". The exit code can: anything but STILL_ACTIVE = zombie.
            if (GetExitCodeProcess(handle, out var exit) && exit != StillActive)
            {
                win32Error = ErrorInvalidParameter;
                return null;
            }
            if (!GetProcessIoCounters(handle, out var counters))
            {
                win32Error = Marshal.GetLastWin32Error();
                return null;
            }
            win32Error = 0;
            return counters.ReadTransferCount;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    private const uint StillActive = 259;
}
