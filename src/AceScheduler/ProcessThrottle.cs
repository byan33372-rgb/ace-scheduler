using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AceScheduler;

public sealed record ThrottleSnapshot(uint PriorityClass, IntPtr Affinity, int? IoPriority);

/// <summary>
/// Resource scheduling via raw, minimal-rights handles (SET_INFORMATION |
/// QUERY) - never depends on .NET's full-access Process handle, which fails
/// outright on protected processes.
/// Throttle = CPU priority Idle + IO priority VeryLow (NtSetInformationProcess
/// ProcessIoPriority) + single-core affinity. IO VeryLow is the point: the
/// target's problem is disk IO, not CPU (docs/design.md #5/#12).
/// The pre-throttle state (priority class + affinity + IO priority) is
/// snapshotted and written back on release - never a hardcoded "Normal".
/// Returns null on success, a human-readable error string on failure. Failures
/// are surfaced to the engine, never swallowed.
/// </summary>
public static class ProcessThrottle
{
    private const uint RightQueryInformation = 0x0400;
    private const uint RightSetInformation = 0x0200; // NOT 0x0800 - that is PROCESS_SUSPEND_RESUME
    private const uint RightQueryLimited = 0x1000;
    private const uint RightsNeeded = RightSetInformation | RightQueryInformation | RightQueryLimited;

    private const int ProcessIoPriority = 0x21;

    private static readonly Dictionary<int, ThrottleSnapshot> Snapshots = new();

    public static string? Apply(int processId, bool throttle, IntPtr fullAffinityMask)
    {
        var handle = OpenProcess(RightsNeeded, false, processId);
        if (handle == IntPtr.Zero) return $"OpenProcess failed (err {Marshal.GetLastWin32Error()}) - protected or gone";
        try
        {
            return throttle ? Throttle(processId, handle, fullAffinityMask) : Release(processId, handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Drop a stale snapshot after the target exits unthrottled-on-our-watch.</summary>
    public static void Forget(int processId) => Snapshots.Remove(processId);

    /// <summary>Forget that also reports whether a snapshot existed (target died on our watch).</summary>
    public static bool ForgetIfPresent(int processId, out bool wasThrottled)
    {
        wasThrottled = Snapshots.Remove(processId);
        return true;
    }

    private static string? Throttle(int pid, IntPtr handle, IntPtr fullAffinityMask)
    {
        if (!Snapshots.TryGetValue(pid, out var snap))
        {
            var prio = GetPriorityClass(handle);
            if (prio == 0) return $"GetPriorityClass failed (err {Marshal.GetLastWin32Error()})";
            if (!GetProcessAffinityMask(handle, out var affinity, out _)) return $"GetProcessAffinityMask failed (err {Marshal.GetLastWin32Error()})";
            snap = new ThrottleSnapshot(prio, affinity, QueryIoPriority(handle));
            Snapshots[pid] = snap;
        }

        if (!SetPriorityClass(handle, 0x40 /* Idle */)) return $"SetPriorityClass Idle failed (err {Marshal.GetLastWin32Error()})";
        var ioErr = SetIoPriority(handle, 0 /* VeryLow */);
        if (!SetProcessAffinityMask(handle, (IntPtr)1))
        {
            // Partial failure: priority+IO are already applied and would stay
            // applied forever (the engine never enters THROTTLED, so nothing
            // releases them). Roll back to the snapshot before reporting.
            var rb = Rollback(handle, snap);
            return $"SetProcessAffinityMask failed (err {Marshal.GetLastWin32Error()})"
                + (rb is null ? "; rolled back to pre-throttle state" : $"; ROLLBACK FAILED ({rb}) - priority may be stuck at Idle");
        }

        // verify what we can read back
        var now = GetPriorityClass(handle);
        if (now == 0) return $"GetPriorityClass failed (err {Marshal.GetLastWin32Error()})";
        if (now != 0x40)
        {
            var rb = Rollback(handle, snap);
            return $"verify failed: priority class readback 0x{now:X}, expected 0x40"
                + (rb is null ? "; rolled back to pre-throttle state" : $"; ROLLBACK FAILED ({rb})");
        }
        return ioErr; // null = IO priority applied too
    }

    /// <summary>Best-effort restore of everything Throttle may have applied. Null = fully restored.</summary>
    private static string? Rollback(IntPtr handle, ThrottleSnapshot snap)
    {
        var errors = new List<string>();
        if (!SetPriorityClass(handle, snap.PriorityClass)) errors.Add($"priority err {Marshal.GetLastWin32Error()}");
        if (snap.IoPriority.HasValue && SetIoPriority(handle, snap.IoPriority.Value) is { } ioErr) errors.Add(ioErr);
        if (!SetProcessAffinityMask(handle, snap.Affinity)) errors.Add($"affinity err {Marshal.GetLastWin32Error()}");
        return errors.Count == 0 ? null : string.Join(", ", errors);
    }

    private static string? Release(int pid, IntPtr handle)
    {
        if (!Snapshots.TryGetValue(pid, out var snap))
        {
            return null; // never throttled by us - nothing to restore
        }
        if (!SetPriorityClass(handle, snap.PriorityClass)) return $"restore priority failed (err {Marshal.GetLastWin32Error()})";
        if (snap.IoPriority.HasValue)
        {
            var ioErr = SetIoPriority(handle, snap.IoPriority.Value);
            if (ioErr != null) return "restore io priority: " + ioErr;
        }
        if (!SetProcessAffinityMask(handle, snap.Affinity)) return $"restore affinity failed (err {Marshal.GetLastWin32Error()})";

        var now = GetPriorityClass(handle);
        if (now == 0) return $"GetPriorityClass failed (err {Marshal.GetLastWin32Error()})";
        if (now != snap.PriorityClass) return "restore verify failed";
        Snapshots.Remove(pid);
        return null;
    }

    public static IntPtr FullAffinityMask()
        => Environment.ProcessorCount >= 64
            ? (IntPtr)(-1L) // all bits; (1L<<64) would shift to 1 and yield mask 0
            : (IntPtr)((1L << Environment.ProcessorCount) - 1);

    private static int? QueryIoPriority(IntPtr handle)
    {
        var v = 5; int ret;
        var st = NtQueryInformationProcess(handle, ProcessIoPriority, ref v, 4, out ret);
        return st == 0 ? v : (int?)null;
    }

    private static string? SetIoPriority(IntPtr handle, int value)
    {
        var v = value;
        var st = NtSetInformationProcess(handle, ProcessIoPriority, ref v, 4);
        return st == 0 ? null : $"NtSetInformationProcess 0x{st:X}";
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

    // GetPriorityClass RETURNS the class (0 = failure) - it is not an out-param API
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr affinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr hProcess, out IntPtr processAffinityMask, out IntPtr systemAffinityMask);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr hProcess, int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass, ref int info, int length, out int returnLength);
}
