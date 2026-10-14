using System.Management;
using System.Runtime.InteropServices;

namespace AceScheduler;

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

    public static double? GetCumulativeReadBytes(int processId)
    {
        try
        {
            if (_mode == 0) Decide(processId);
            return _mode switch
            {
                1 => Win32ReadBytes(processId) ?? WmiReadBytes(processId),
                _ => WmiReadBytes(processId),
            };
        }
        catch
        {
            return null;
        }
    }

    private static void Decide(int processId)
    {
        var win32 = Win32ReadBytes(processId);
        if (win32.HasValue)
        {
            _mode = 1;
            return;
        }
        if (WmiReadBytes(processId).HasValue)
        {
            _mode = 2;
        }
        // both failed: target likely gone either way; stay undecided and retry
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

    private static double? Win32ReadBytes(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessIoCounters(handle, out var counters)) return null;
            return counters.ReadTransferCount;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
