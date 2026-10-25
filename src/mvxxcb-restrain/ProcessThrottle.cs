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
/// Recovery safety:
/// - all-or-nothing: any failed write rolls back every applied property.
/// - Release restores all three properties independently with readback
///   verification (IO verified when queryable); partial failure keeps the
///   snapshot for retry.
/// - PID identity: every operation on an existing snapshot first verifies the
///   live process creation time; a mismatch (pid reuse) discards the snapshot
///   instead of writing it onto the innocent process.
/// - Journal trust boundary: entries are validated on read (creation time
///   present, priority class in the legal set, affinity within the system
///   mask, IO priority in range, name in the scanner allowlist, bounded
///   size/count). Invalid entries are dropped loudly.
/// - Startup recovery is transactional: the journal is never cleared up
///   front; each entry's fate is decided individually (restored/gone ->
///   removed; access denied/other failure -> KEPT for the next attempt) and
///   the remainder is written back atomically.
/// - Fail-closed: when the journal cannot be persisted, new throttles are
///   refused (an unrecoverable throttle is worse than no throttle).
/// </summary>
public static class ProcessThrottle
{
    private const uint RightQueryInformation = 0x0400;      // GetPriorityClass needs this
    private const uint RightSetInformation = 0x0200;        // NOT 0x0800 - that is PROCESS_SUSPEND_RESUME
    private const uint RightQueryLimited = 0x1000;
    private const uint RightsNeeded = RightSetInformation | RightQueryInformation | RightQueryLimited;

    private const int ProcessIoPriority = 0x21;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    private static readonly uint[] LegalPriorityClasses =
    {
        0x0020, // NORMAL
        0x0040, // IDLE
        0x0080, // HIGH
        0x0100, // REALTIME
        0x4000, // BELOW_NORMAL
        0x8000, // ABOVE_NORMAL
    };

    private static readonly Dictionary<int, ThrottleSnapshot> Snapshots = new();
    private static readonly object Gate = new();
    private static string? _journalPath;
    private static bool _journalHealthy = true;
    private static bool _journalFailureLogged;

    private sealed record JournalEntry(int pid, string name, long? createdAt, uint priorityClass, long affinity, int? ioPriority);
    private sealed record JournalFile(List<JournalEntry> entries);

    public enum RecoverOutcome { Restored, Gone, PidReused, KeptForRetry }

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
    /// Best-effort release before dropping a target. Only a confirmed
    /// restore or a confirmed death may discard the snapshot; anything else
    /// (access denied, partial restore) keeps it journaled so the next
    /// startup or re-adoption can retry. Returns the outcome plus an error
    /// text when the restore attempt failed.
    /// </summary>
    public static (RecoverOutcome Outcome, string? Error) TryRecoverBeforeForget(int processId, string processName, IntPtr fullAffinityMask)
    {
        lock (Gate)
        {
            if (!Snapshots.ContainsKey(processId)) return (RecoverOutcome.Gone, null); // nothing of ours to restore
        }
        var handle = OpenProcess(RightsNeeded, false, processId);
        if (handle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorAccessDenied)
            {
                return (RecoverOutcome.KeptForRetry, $"OpenProcess access denied (err 5) - snapshot kept journaled for the next startup");
            }
            if (err == ErrorInvalidParameter)
            {
                Forget(processId); // pid confirmed gone
                return (RecoverOutcome.Gone, null);
            }
            // transient/unknown error: keep the record, do not gamble it away
            return (RecoverOutcome.KeptForRetry, $"OpenProcess err {err} - snapshot kept journaled for the next startup");
        }
        try
        {
            if (!CreationMatches(handle, processId, out var mismatchReason) && mismatchReason is not null)
            {
                Forget(processId); // pid reused: the innocent process must not receive the old snapshot
                return (RecoverOutcome.PidReused, mismatchReason);
            }
            var err2 = Apply(processId, processName, false, fullAffinityMask);
            if (err2 is null)
            {
                Forget(processId);
                return (RecoverOutcome.Restored, null);
            }
            return (RecoverOutcome.KeptForRetry, err2); // keep the snapshot for retry
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Drop a snapshot unconditionally (only after confirmed restore/gone).</summary>
    public static void Forget(int processId)
    {
        lock (Gate)
        {
            Snapshots.Remove(processId);
            WriteJournalLocked();
        }
    }

    /// <summary>
    /// Transactional startup recovery. The journal is validated, then each
    /// entry decides its own fate: restored / confirmed gone / pid reused ->
    /// removed; access denied / partial restore failure -> KEPT. The
    /// remainder is written back atomically, so a non-elevated start against
    /// a protected target loses nothing (the next elevated start retries).
    /// Returns (kept_entries, restored, dropped_invalid).
    /// </summary>
    public static (int kept, int restored, int droppedInvalid) RecoverAtStartup()
    {
        var (entries, droppedInvalid) = ReadJournalValidated();
        if (entries.Count == 0 && droppedInvalid == 0) return (0, 0, droppedInvalid);
        var remaining = new List<JournalEntry>(entries);
        var restored = 0;

        lock (Gate) Snapshots.Clear();

        foreach (var e in entries)
        {
            var handle = OpenProcess(RightsNeeded, false, e.pid);
            if (handle == IntPtr.Zero)
            {
                var openErr = Marshal.GetLastWin32Error();
                if (openErr == ErrorAccessDenied)
                {
                    FileLogger.Write($"journal recovery: pid={e.pid} access denied (run elevated?) - entry KEPT for the next start");
                    continue; // keep
                }
                remaining.Remove(e); // confirmed gone (err 87 etc.)
                continue;
            }
            try
            {
                if (!GetCreationFiletime(handle, out var created))
                {
                    // identity unreadable on a LIVE process: never write, but
                    // never lose the record either - keep and retry later
                    FileLogger.Write($"journal recovery: pid={e.pid} creation time unreadable - entry KEPT for the next start");
                    continue;
                }
                if (e.createdAt.HasValue && created != e.createdAt)
                {
                    remaining.Remove(e); // pid reused: never write
                    FileLogger.Write($"journal recovery: pid={e.pid} identity mismatch (reused pid) - entry dropped");
                    continue;
                }
                // the journal's name field is attacker-controlled text; only
                // the LIVE process's real image name decides (review P0-A)
                var realName = GetImageBaseName(handle);
                if (realName is null || !TargetDiscovery.ScannerProcessNames.Contains(realName, StringComparer.OrdinalIgnoreCase))
                {
                    remaining.Remove(e); // forged or no longer a scanner: refuse to touch it
                    FileLogger.Write($"journal recovery: pid={e.pid} real image '{realName ?? "?"}' not in scanner allowlist - entry dropped, process untouched");
                    continue;
                }
                var snap = new ThrottleSnapshot(e.priorityClass, (IntPtr)e.affinity, e.ioPriority, e.createdAt, e.name);
                var err = RestoreProperties(handle, snap);
                if (err is null)
                {
                    remaining.Remove(e);
                    restored++;
                }
                else
                {
                    FileLogger.Write($"journal recovery: pid={e.pid} partial failure ({err}) - entry KEPT for retry");
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // kept entries are re-registered as snapshots: they survive every
        // later journal write (which serializes Snapshots), and if the same
        // pid is re-adopted with a matching creation time, Throttle reuses
        // the ORIGINAL values instead of snapshotting the polluted state
        // (review P0-C)
        lock (Gate)
        {
            Snapshots.Clear();
            foreach (var e in remaining)
            {
                Snapshots[e.pid] = new ThrottleSnapshot(e.priorityClass, (IntPtr)e.affinity, e.ioPriority, e.createdAt, e.name);
            }
            WriteJournalLocked();
        }
        return (remaining.Count, restored, droppedInvalid);
    }

    private static string? Throttle(int pid, string name, IntPtr handle, IntPtr fullAffinityMask)
    {
        ThrottleSnapshot snap;
        lock (Gate)
        {
            if (!Snapshots.TryGetValue(pid, out var existing))
            {
                if (!_journalHealthy)
                {
                    return "journal unavailable - refusing to throttle (release would be unrecoverable); fix journal access and retry";
                }
                var prio = GetPriorityClass(handle);
                if (prio == 0) return $"GetPriorityClass failed (err {Marshal.GetLastWin32Error()})";
                if (!GetProcessAffinityMask(handle, out var affinity, out _)) return $"GetProcessAffinityMask failed (err {Marshal.GetLastWin32Error()})";
                if (!GetCreationFiletime(handle, out var createdAt))
                {
                    // identity unknown: never throttle what we cannot later identify
                    return "GetProcessTimes failed - cannot establish process identity, refusing to throttle";
                }
                if (prio == 0x40 /* Idle */ && affinity.ToInt64() == 1)
                {
                    // already wearing our throttle fingerprint while we hold no
                    // snapshot: an orphaned throttle. The originals are
                    // unknowable - say so loudly instead of silently
                    // snapshotting the polluted state.
                    FileLogger.Write($"pid={pid} adopted in an already-throttled state (orphaned throttle, journal missing for it) - snapshot may not reflect the true originals");
                }
                snap = new ThrottleSnapshot(prio, affinity, QueryIoPriority(handle), createdAt, name);
                Snapshots[pid] = snap;
                // fail-closed INCLUDING this first write: a throttle without
                // a persisted recovery record must never be applied
                if (!WriteJournalLocked())
                {
                    Snapshots.Remove(pid);
                    return "journal write failed - refusing to throttle (no recovery record would exist)";
                }
            }
            else
            {
                // pid reuse guard: the live process must still be the one we
                // snapshotted. The snapshot is KEPT (not discarded) so every
                // retry keeps refusing until the worker's identity probe
                // retires the target - discarding it here would let the
                // engine's retry create a fresh snapshot for the new process
                // and throttle it (review P0-D).
                if (existing.CreatedAtFiletime.HasValue
                    && (!GetCreationFiletime(handle, out var nowCreated) || nowCreated != existing.CreatedAtFiletime.Value))
                {
                    FileLogger.Write($"pid={pid} creation time mismatch (pid reused) - refusing to throttle; snapshot kept until re-adoption retires this target");
                    return "pid was reused; refusing to throttle this pid until it is re-adopted";
                }
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
        ThrottleSnapshot snap;
        lock (Gate)
        {
            if (!Snapshots.TryGetValue(pid, out var existing)) return null; // never throttled by us
            snap = existing;
        }
        // pid reuse guard before writing anything
        if (snap.CreatedAtFiletime.HasValue
            && (!GetCreationFiletime(handle, out var created) || created != snap.CreatedAtFiletime.Value))
        {
            FileLogger.Write($"pid={pid} creation time mismatch at release (pid reused) - snapshot discarded, new process untouched");
            Forget(pid);
            return null; // the innocent process keeps its own state; treat as released
        }
        var err = RestoreProperties(handle, snap);
        if (err is null)
        {
            lock (Gate)
            {
                Snapshots.Remove(pid);
                WriteJournalLocked();
            }
            return null;
        }
        lock (Gate)
        {
            Snapshots[pid] = snap; // keep for retry
            WriteJournalLocked();
        }
        return "partial restore failed: " + err + " (snapshot kept for retry)";
    }

    /// <summary>Restores all three properties independently. Returns error text on partial failure.</summary>
    private static string? RestoreProperties(IntPtr handle, ThrottleSnapshot snap)
    {
        var errors = new List<string>();

        if (!SetPriorityClass(handle, snap.PriorityClass)) errors.Add($"priority err {Marshal.GetLastWin32Error()}");
        else if (GetPriorityClass(handle) != snap.PriorityClass) errors.Add("priority readback mismatch");

        if (snap.IoPriority.HasValue)
        {
            if (SetIoPriority(handle, snap.IoPriority.Value) is { } ioErr) errors.Add("io: " + ioErr);
            else if (QueryIoPriority(handle) is { } q && q != snap.IoPriority.Value) errors.Add($"io readback mismatch (now {q}, want {snap.IoPriority.Value})");
        }

        if (!SetProcessAffinityMask(handle, snap.Affinity)) errors.Add($"affinity err {Marshal.GetLastWin32Error()}");
        else if (GetProcessAffinityMask(handle, out var affinityNow, out _) && affinityNow != snap.Affinity)
            errors.Add("affinity readback mismatch");

        return errors.Count == 0 ? null : string.Join(", ", errors);
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

    // ---------------- pid identity ----------------

    /// <summary>True when the live process still matches the snapshotted identity.</summary>
    private static bool CreationMatches(IntPtr handle, int pid, out string? reason)
    {
        ThrottleSnapshot snap;
        lock (Gate)
        {
            if (!Snapshots.TryGetValue(pid, out var existing)) { reason = null; return true; }
            snap = existing;
        }
        if (!snap.CreatedAtFiletime.HasValue) { reason = null; return true; }
        if (!GetCreationFiletime(handle, out var created) || created != snap.CreatedAtFiletime.Value)
        {
            reason = $"pid={pid} creation time mismatch (pid reused)";
            return false;
        }
        reason = null;
        return true;
    }

    private static bool GetCreationFiletime(IntPtr handle, out long filetime)
    {
        // parameter order is creation, exit, kernel, USER - taking the fourth
        // slot silently hands back CPU time, which changes every sample
        if (GetProcessTimes(handle, out var creation, out _, out _, out _)
            && (creation.dwLowDateTime != 0 || creation.dwHighDateTime != 0))
        {
            filetime = ((long)creation.dwHighDateTime << 32) | creation.dwLowDateTime;
            return true;
        }
        filetime = 0;
        return false;
    }

    /// <summary>Base name (no extension) of the LIVE process image; null when unreadable.</summary>
    private static string? GetImageBaseName(IntPtr handle)
    {
        var buf = new char[512];
        var len = buf.Length;
        if (!QueryFullProcessImageName(handle, 0, buf, ref len)) return null;
        return Path.GetFileNameWithoutExtension(new string(buf, 0, len));
    }

    // ---------------- journal ----------------

    /// <summary>Serialize the snapshot table. Returns false when the write failed (fail-closed callers must not throttle then).</summary>
    private static bool WriteJournalLocked()
    {
        var entries = Snapshots.Select(kv =>
            new JournalEntry(kv.Key, kv.Value.Name, kv.Value.CreatedAtFiletime, kv.Value.PriorityClass, kv.Value.Affinity.ToInt64(), kv.Value.IoPriority)).ToList();
        return WriteJournalEntriesLocked(entries);
    }

    private static bool WriteJournalEntriesLocked(List<JournalEntry> entries)
    {
        if (_journalPath is null) return true; // no journal configured (selftest)
        try
        {
            var tmp = _journalPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new JournalFile(entries)));
            File.Move(tmp, _journalPath, overwrite: true);
            _journalHealthy = true;
            return true;
        }
        catch
        {
            _journalHealthy = false;
            if (!_journalFailureLogged)
            {
                _journalFailureLogged = true;
                FileLogger.Write("journal write failed - further throttles will be refused until the journal is writable (fail-closed)");
            }
            return false;
        }
    }

    /// <summary>
    /// Read and validate the journal. Entries failing validation are dropped
    /// and counted: the journal lives in a user-writable directory, so it is
    /// untrusted input (review P0-A). Oversized/too-many/corrupt files are
    /// quarantined (.corrupt-&lt;stamp&gt;) instead of being silently ignored.
    /// </summary>
    private static (List<JournalEntry> entries, int dropped) ReadJournalValidated()
    {
        try
        {
            if (_journalPath is null || !File.Exists(_journalPath)) return (new List<JournalEntry>(), 0);
            var fi = new FileInfo(_journalPath);
            if (fi.Length > 64 * 1024)
            {
                Quarantine($"oversized ({fi.Length}B)");
                return (new List<JournalEntry>(), 0);
            }
            JournalFile? file;
            try
            {
                file = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(_journalPath));
            }
            catch (Exception ex)
            {
                Quarantine($"unparseable ({ex.Message})");
                return (new List<JournalEntry>(), 0);
            }
            var raw = file?.entries ?? new List<JournalEntry>();
            if (raw.Count > 64)
            {
                Quarantine($"too many entries ({raw.Count})");
                return (new List<JournalEntry>(), raw.Count);
            }
            // affinity bounds: fall back to a permissive mask when the query
            // fails - a failed check must not mass-reject valid entries
            if (!GetProcessAffinityMask(GetCurrentProcess(), out _, out var systemMask))
            {
                systemMask = FullAffinityMask();
            }
            var valid = new List<JournalEntry>();
            var dropped = 0;
            foreach (var e in raw)
            {
                var bad = e.pid <= 0
                    || !e.createdAt.HasValue || e.createdAt.Value <= 0
                    || !LegalPriorityClasses.Contains(e.priorityClass)
                    || e.affinity == 0 || (e.affinity & ~systemMask.ToInt64()) != 0
                    || (e.ioPriority.HasValue && (e.ioPriority.Value < 0 || e.ioPriority.Value > 4))
                    || !TargetDiscovery.ScannerProcessNames.Contains(e.name, StringComparer.OrdinalIgnoreCase);
                if (bad)
                {
                    dropped++;
                    FileLogger.Write($"journal entry rejected (pid={e.pid}, name={e.name}, prio=0x{e.priorityClass:X}, createdAt={(e.createdAt?.ToString() ?? "null")}) - failed validation, dropped");
                    continue;
                }
                valid.Add(e);
            }
            if (dropped > 0) FileLogger.Write($"journal validation: kept {valid.Count}, dropped {dropped} invalid entr{(dropped == 1 ? "y" : "ies")}");
            return (valid, dropped);

            void Quarantine(string why)
            {
                FileLogger.Write($"journal {why} - quarantined as .corrupt-<stamp>, starting empty");
                try
                {
                    File.Move(_journalPath!, _journalPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                }
                catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Write($"journal read error ({ex.Message}) - treated as empty; original left on disk");
            return (new List<JournalEntry>(), 0);
        }
    }

    // ---------------- raw properties ----------------

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, char[] exeName, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr hProcess, int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass, ref int info, int length, out int returnLength);
}
