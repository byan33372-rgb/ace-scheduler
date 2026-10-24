using System.Runtime.InteropServices;
using System.Text.Json;

namespace MvxxcbRestrain;

public sealed record ThrottleSnapshot(uint PriorityClass, IntPtr Affinity, int? IoPriority, long? CreatedAtFiletime, string Name);

/// <summary>
/// Resource scheduling via raw handles (SET_INFORMATION | QUERY_INFORMATION |
/// QUERY_LIMITED). Throttle = CPU Idle + IO VeryLow (only when the snapshot
/// captured the original IO priority - otherwise we could never restore it)
/// + single-core affinity.
///
/// Recovery safety (external review P0-1/P0-2):
/// - all-or-nothing: any failed write rolls back every applied property.
/// - Release attempts all three properties independently and verifies each
///   readback; the snapshot survives a partial failure for retry.
/// - Snapshots are journaled to disk (pid + creation time + originals). On
///   startup the journal is replayed: live processes with matching creation
///   time are restored, stale/reused pids are dropped. This covers crashes,
///   kills, logoff - anything that skips the graceful release path.
/// </summary>
public static class ProcessThrottle
{
    private const uint RightQueryInformation = 0x0400;      // GetPriorityClass needs this
    private const uint RightSetInformation = 0x0200;        // NOT 0x0800 - that is PROCESS_SUSPEND_RESUME
    private const uint RightQueryLimited = 0x1000;
    private const uint RightsNeeded = RightSetInformation | RightQueryInformation | RightQueryLimited;

    private const int ProcessIoPriority = 0x21;

    private static readonly Dictionary<int, ThrottleSnapshot> Snapshots = new();
    private static readonly object Gate = new();
    private static string? _journalPath;

    private sealed record JournalEntry(int pid, string name, long? createdAt, uint priorityClass, long affinity, int? ioPriority);
    private sealed record JournalFile(List<JournalEntry> entries);

    /// <summary>Point the journal at its file; called once at startup.</summary>
    public static void Configure(string journalPath) => _journalPath = journalPath;

    /// <summary>Apply or release throttling. Returns null on success, an error text otherwise.</summary>
    public static string? Apply(int processId, string processName, bool throttle, IntPtr fullAffinityMask)
    {
        var handle = OpenProcess(RightsNeeded, false, processId);
        if (handle == IntPtr.Zero) return $"OpenProcess failed (err {Marshal.GetLastWin32Error()}) - protected or gone";
        try
        {
            return throttle ? Throttle(processId, processName, handle, fullAffinityMask) : Release(processId, handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Best-effort release before dropping a target for good (process dying).
    /// Restore may be impossible; we still clear the snapshot and journal so
    /// a reused pid can never inherit it.
    /// </summary>
    public static string? TryRecoverBeforeForget(int processId, string processName, IntPtr fullAffinityMask)
    {
        var err = Apply(processId, processName, false, fullAffinityMask);
        Forget(processId);
        return err;
    }

    /// <summary>Drop a snapshot unconditionally (used after successful release paths).</summary>
    public static void Forget(int processId)
    {
        lock (Gate)
        {
            Snapshots.Remove(processId);
            WriteJournalLocked();
        }
    }

    /// <summary>
    /// Replay the startup journal: restore live processes whose creation time
    /// matches (protects against pid reuse), drop everything else. Returns
    /// (attempted, restored).
    /// </summary>
    public static (int attempted, int restored) RecoverAtStartup()
    {
        var entries = ReadJournal();
        if (entries.Count == 0) return (0, 0);
        // start clean; re-add only entries that fail to restore
        lock (Gate)
        {
            Snapshots.Clear();
            WriteJournalLocked();
        }
        var restored = 0;
        foreach (var e in entries)
        {
            var handle = OpenProcess(RightsNeeded, false, e.pid);
            if (handle == IntPtr.Zero) continue; // gone for good
            try
            {
                if (!GetCreationFiletime(handle, out var created)) continue;
                if (e.createdAt.HasValue && created != e.createdAt) continue; // pid reused by an innocent process
                var snap = new ThrottleSnapshot(e.priorityClass, (IntPtr)e.affinity, e.ioPriority, e.createdAt, e.name);
                if (ReleaseWith(e.pid, handle, snap) is null)
                {
                    restored++;
                }
                else
                {
                    // partial failure: keep the snapshot so a later manual
                    // disable/exit can retry the restore
                    lock (Gate)
                    {
                        Snapshots[e.pid] = snap;
                        WriteJournalLocked();
                    }
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        return (entries.Count, restored);
    }

    private static string? Throttle(int pid, string name, IntPtr handle, IntPtr fullAffinityMask)
    {
        ThrottleSnapshot snap;
        lock (Gate)
        {
            if (!Snapshots.TryGetValue(pid, out var existing))
            {
                var prio = GetPriorityClass(handle);
                if (prio == 0) return $"GetPriorityClass failed (err {Marshal.GetLastWin32Error()})";
                if (!GetProcessAffinityMask(handle, out var affinity, out _)) return $"GetProcessAffinityMask failed (err {Marshal.GetLastWin32Error()})";
                if (prio == 0x40 /* Idle */ && affinity.ToInt64() == 1)
                {
                    // already wearing our throttle fingerprint while we hold no
                    // snapshot: an orphaned throttle from a crashed/lost
                    // journal. The originals are unknowable - say so loudly
                    // instead of silently snapshotting the polluted state.
                    FileLogger.Write($"pid={pid} adopted in an already-throttled state (orphaned throttle, journal missing for it) - snapshot may not reflect the true originals");
                }
                GetCreationFiletime(handle, out var createdAt);
                snap = new ThrottleSnapshot(prio, affinity, QueryIoPriority(handle), createdAt, name);
                Snapshots[pid] = snap;
                WriteJournalLocked();
            }
            else
            {
                snap = existing;
            }
        }

        // all-or-nothing: any failed write rolls back everything applied so far
        string FailWithRollback(string error)
        {
            var rb = Rollback(handle, snap);
            return error + (rb is null ? "; rolled back to pre-throttle state" : $"; ROLLBACK FAILED ({rb}) - snapshot kept");
        }

        if (!SetPriorityClass(handle, 0x40 /* Idle */))
            return FailWithRollback($"SetPriorityClass Idle failed (err {Marshal.GetLastWin32Error()})");
        if (snap.IoPriority.HasValue && SetIoPriority(handle, 0 /* VeryLow */) is { } ioErr)
            return FailWithRollback($"SetIoPriority VeryLow failed: {ioErr}");
        if (!SetProcessAffinityMask(handle, (IntPtr)1))
            return FailWithRollback($"SetProcessAffinityMask failed (err {Marshal.GetLastWin32Error()})");

        var now = GetPriorityClass(handle);
        if (now == 0) return FailWithRollback($"GetPriorityClass failed (err {Marshal.GetLastWin32Error()})");
        if (now != 0x40) return FailWithRollback($"verify failed: priority class readback 0x{now:X}, expected 0x40");
        return null;
    }

    private static string? Release(int pid, IntPtr handle)
    {
        lock (Gate)
        {
            if (!Snapshots.TryGetValue(pid, out var snap)) return null; // never throttled by us
            return ReleaseWith(pid, handle, snap);
        }
    }

    /// <summary>Restores all three properties independently; snapshot survives partial failure.</summary>
    private static string? ReleaseWith(int pid, IntPtr handle, ThrottleSnapshot snap)
    {
        var errors = new List<string>();

        if (!SetPriorityClass(handle, snap.PriorityClass)) errors.Add($"priority err {Marshal.GetLastWin32Error()}");
        else if (GetPriorityClass(handle) != snap.PriorityClass) errors.Add("priority readback mismatch");

        if (snap.IoPriority.HasValue)
        {
            if (SetIoPriority(handle, snap.IoPriority.Value) is { } ioErr) errors.Add("io: " + ioErr);
        }

        if (!SetProcessAffinityMask(handle, snap.Affinity)) errors.Add($"affinity err {Marshal.GetLastWin32Error()}");
        else if (GetProcessAffinityMask(handle, out var affinityNow, out _) && affinityNow != snap.Affinity)
            errors.Add("affinity readback mismatch");

        if (errors.Count > 0)
        {
            lock (Gate)
            {
                Snapshots[pid] = snap; // keep for retry
                WriteJournalLocked();
            }
            return "partial restore failed: " + string.Join(", ", errors) + " (snapshot kept for retry)";
        }
        lock (Gate)
        {
            Snapshots.Remove(pid);
            WriteJournalLocked();
        }
        return null;
    }

    /// <summary>Best-effort restore of everything Throttle may have applied.</summary>
    private static string? Rollback(IntPtr handle, ThrottleSnapshot snap)
    {
        var errors = new List<string>();
        if (!SetPriorityClass(handle, snap.PriorityClass)) errors.Add($"priority err {Marshal.GetLastWin32Error()}");
        if (snap.IoPriority.HasValue && SetIoPriority(handle, snap.IoPriority.Value) is { } ioErr) errors.Add(ioErr);
        if (!SetProcessAffinityMask(handle, snap.Affinity)) errors.Add($"affinity err {Marshal.GetLastWin32Error()}");
        return errors.Count == 0 ? null : string.Join(", ", errors);
    }

    public static IntPtr FullAffinityMask()
        => Environment.ProcessorCount >= 64
            ? (IntPtr)(-1L) // all bits; (1L<<64) would shift to 1 and yield mask 0
            : (IntPtr)((1L << Environment.ProcessorCount) - 1);

    // ---------------- journal ----------------

    private static void WriteJournalLocked()
    {
        if (_journalPath is null) return;
        try
        {
            var file = new JournalFile(Snapshots.Select(kv =>
            {
                var s = kv.Value;
                return new JournalEntry(kv.Key, s.Name, s.CreatedAtFiletime, s.PriorityClass, s.Affinity.ToInt64(), s.IoPriority);
            }).ToList());
            var tmp = _journalPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file));
            File.Move(tmp, _journalPath, overwrite: true);
        }
        catch
        {
            // journal failure must not break scheduling; recovery degrades to in-memory only
        }
    }

    private static List<JournalEntry> ReadJournal()
    {
        try
        {
            if (_journalPath is null || !File.Exists(_journalPath)) return new List<JournalEntry>();
            var file = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(_journalPath));
            return file?.entries ?? new List<JournalEntry>();
        }
        catch
        {
            return new List<JournalEntry>(); // corrupt journal: treat as empty
        }
    }

    // ---------------- process identity ----------------

    private static bool GetCreationFiletime(IntPtr handle, out long filetime)
    {
        // parameter order is creation, exit, kernel, USER - taking the fourth
        // slot silently hands back CPU time, which changes every sample and
        // made every startup recovery mismatch (0/N restored in field test)
        if (GetProcessTimes(handle, out var creation, out _, out _, out _)
            && (creation.dwLowDateTime != 0 || creation.dwHighDateTime != 0))
        {
            filetime = ((long)creation.dwHighDateTime << 32) | creation.dwLowDateTime;
            return true;
        }
        filetime = 0;
        return false;
    }

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
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

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
