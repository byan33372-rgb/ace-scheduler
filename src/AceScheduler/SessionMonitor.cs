using System.Diagnostics;
using System.Management;
using System.Text;

namespace AceScheduler;

/// <summary>
/// Observes scanner sessions: when scanners appear/disappear, what the
/// scheduler did, when games exit, and what happens AFTER the game exits
/// (the "tail" - how long the scan keeps running and how much it reads).
/// Writes a JSONL timeline per session plus a human-readable Chinese summary
/// once the scanner has been quiet for 2 minutes. Read-only observation:
/// it never changes scheduler behavior.
/// </summary>
public sealed class SessionMonitor
{
    private sealed class TrackedScan
    {
        public string Name = "";
        public double EstReadMB;
        public double PeakMBps;
        public int Throttles;
        public int Releases;
        public int Passthroughs;
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, TrackedScan> _scans = new();
    private readonly HashSet<string> _gamesPresent = new(StringComparer.OrdinalIgnoreCase);
    private string? _sessionFile;
    private DateTimeOffset? _sessionStart;
    private DateTimeOffset? _lastScanSeen;
    private DateTimeOffset? _gameExitAt; // most recent moment the last game left while a scanner was alive
    private DateTimeOffset? _gameStartAt;

    /// <summary>Game executables we annotate in the timeline (monitoring only, not control).</summary>
    public static readonly string[] GameNames = { "VALORANT-Win64-Shipping", "Endfield" };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mvxxcb-restrain", "sessions");

    private void EnsureSession(DateTimeOffset now)
    {
        if (_sessionFile is not null) return;
        _sessionStart = now;
        try
        {
            Directory.CreateDirectory(Dir);
            _sessionFile = Path.Combine(Dir, $"session-{now:yyyyMMdd-HHmmss}.jsonl");
            File.AppendAllText(_sessionFile, J(now, "session-start") + Environment.NewLine);
        }
        catch { _sessionFile = null; }
    }

    private static string J(DateTimeOffset t, string evt, string detail = "") =>
        "{\"ts\":\"" + t.ToString("o") + "\",\"evt\":\"" + evt + "\",\"detail\":\"" + detail.Replace("\"", "'") + "\"}";

    private void Append(string evt, string detail)
    {
        try
        {
            if (_sessionFile is not null)
                File.AppendAllText(_sessionFile, J(DateTimeOffset.Now, evt, detail) + Environment.NewLine);
        }
        catch { }
    }

    public void ScannerFound(int pid, string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureSession(now);
            _scans[pid] = new TrackedScan { Name = name, FirstSeen = now, LastSeen = now };
            _lastScanSeen = now;
        }
        Append("scanner-found", $"pid={pid} name={name}");
    }

    public void ScannerGone(int pid, DateTimeOffset now)
    {
        lock (_gate)
        {
            _scans.Remove(pid);
            _lastScanSeen = now;
        }
        Append("scanner-gone", $"pid={pid}");
    }

    /// <summary>Feed engine samples: rate + current state, ~2Hz. Totals are estimates.</summary>
    public void EngineSample(int pid, double mbps, string state, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_scans.TryGetValue(pid, out var s)) return;
            s.LastSeen = now;
            _lastScanSeen = now;
            s.EstReadMB += mbps * 0.5; // sampler cadence
            if (mbps > s.PeakMBps) s.PeakMBps = mbps;
            switch (state)
            {
                case "Throttled": break;
                case "BurstIdle": break;
            }
        }
    }

    public void EngineEvent(int pid, string kind, string detail)
    {
        lock (_gate)
        {
            if (_scans.TryGetValue(pid, out var s))
            {
                if (kind is "throttle" or "rethrottle") s.Throttles++;
                if (kind is "release" or "scan-ended") s.Releases++;
                if (kind is "passthrough") s.Passthroughs++;
            }
        }
        Append("scheduler-" + kind, $"pid={pid} {detail}");
    }

    public void Log(string evt, string detail) => Append(evt, detail);

    /// <summary>Poll annotated game executables; called ~1Hz from the UI timer.</summary>
    public void PollGames(DateTimeOffset now)
    {
        foreach (var name in GameNames)
        {
            bool present;
            try { present = Process.GetProcessesByName(name).Length > 0; }
            catch { continue; }
            GamePresence(name, present, now);
        }
    }

    public void GamePresence(string name, bool present, DateTimeOffset now)
    {
        bool changed;
        lock (_gate)
        {
            changed = present ? _gamesPresent.Add(name) : _gamesPresent.Remove(name);
            if (changed)
            {
                if (present && _gamesPresent.Count == 1) _gameStartAt = now;
                if (!present && _gamesPresent.Count == 0 && _scans.Count > 0) _gameExitAt = now;
            }
        }
        if (changed) Append(present ? "game-start" : "game-exit", name);
    }

    /// <summary>Close the session once scanners have been gone for 2 minutes; writes the summary.</summary>
    public void TickCloseCheck(DateTimeOffset now)
    {
        DateTimeOffset? lastSeen; bool open;
        lock (_gate) { open = _sessionFile is not null; lastSeen = _lastScanSeen; }
        if (!open) return;
        if (_scans.Count > 0) return;
        if (lastSeen.HasValue && (now - lastSeen.Value).TotalMinutes < 2) return;
        WriteSummary(now);
        lock (_gate) { _sessionFile = null; _sessionStart = null; _scans.Clear(); _lastScanSeen = null; _gameExitAt = null; _gameStartAt = null; }
    }

    public void ForceClose(string reason)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_sessionFile is null) return;
        }
        WriteSummary(now, reason);
        lock (_gate) { _sessionFile = null; _sessionStart = null; _scans.Clear(); _lastScanSeen = null; _gameExitAt = null; _gameStartAt = null; }
    }

    private void WriteSummary(DateTimeOffset now, string reason = "scanner quiet for 2 minutes")
    {
        TrackedScan[] scans; DateTimeOffset? start, gameExit, gameStart;
        lock (_gate)
        {
            scans = _scans.Values.ToArray();
            start = _sessionStart; gameExit = _gameExitAt; gameStart = _gameStartAt;
        }
        var sb = new StringBuilder();
        sb.AppendLine("==================================================");
        sb.AppendLine($"mvxxcb-restrain 监测报告  {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"会话开始: {start:HH:mm:ss}   结束: {now:HH:mm:ss}   关闭原因: {reason}");
        if (gameStart.HasValue) sb.AppendLine($"游戏运行: 自 {gameStart:HH:mm:ss}" + (gameExit.HasValue ? $" 至 {gameExit:HH:mm:ss}" : "（仍在运行）"));
        if (gameExit.HasValue && _lastScanSeen.HasValue)
        {
            var tail = _lastScanSeen.Value - gameExit.Value;
            sb.AppendLine($"★ 游戏退出后，扫描进程仍活跃了约 {tail.TotalMinutes:F1} 分钟");
        }
        else if (scans.Length == 0 && gameExit.HasValue)
        {
            sb.AppendLine("游戏退出后未观察到扫描进程活动");
        }
        sb.AppendLine($"本次调度: 限速施加 {scans.Sum(s => s.Throttles)} 次, 解除 {scans.Sum(s => s.Releases)} 次, 空闲放行 {scans.Sum(s => s.Passthroughs)} 次");
        foreach (var s in scans)
        {
            sb.AppendLine($"  - {s.Name}: 活跃 {s.FirstSeen:HH:mm:ss}→{s.LastSeen:HH:mm:ss}, 估算读取 {s.EstReadMB / 1024:F2} GB, 峰值 {s.PeakMBps:F0} MB/s");
        }
        sb.AppendLine($"磁盘状态: {DiskStatusLine()}");
        sb.AppendLine($"明细时间线: {Path.GetFileName(_sessionFile)}");
        sb.AppendLine();
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "summary.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
        Append("session-summary", "written to summary.txt");
    }

    private static string DiskStatusLine()
    {
        try
        {
            var parts = new List<string>();
            using var searcher = new ManagementObjectSearcher("SELECT Model,Status,Size FROM Win32_DiskDrive");
            foreach (var o in searcher.Get())
            {
                var sizeGb = o["Size"] is null ? 0 : Convert.ToDouble(o["Size"]) / 1073741824;
                parts.Add($"{o["Model"]} ({sizeGb:F0}GB): {o["Status"]}");
            }
            return parts.Count == 0 ? "unknown" : string.Join("; ", parts);
        }
        catch { return "unknown (WMI unavailable)"; }
    }
}
