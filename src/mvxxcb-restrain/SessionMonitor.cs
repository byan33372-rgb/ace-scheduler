using System.Management;
using System.Security.Principal;
using System.Text;

namespace MvxxcbRestrain;

/// <summary>
/// Observes scanner sessions: scanner presence, per-sample rates (persisted
/// to the timeline), scheduler actions, foreground-fullscreen occupancy,
/// game start/exit, and the post-game "tail". Writes a JSONL timeline plus a
/// Chinese summary once scanners have been quiet for 2 minutes.
/// Read-only observation: it never changes scheduler behavior.
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
        public int Samples;
        public int DeniedSamples;
        public double ThrottledSeconds;
        public bool IsThrottled;
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
        public DateTimeOffset LastRateLog;
        public DateTimeOffset LastDenyLog;
        public double EstReadMBAtGameExit; // snapshot for tail volume
        public bool MarkedGameExit;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, TrackedScan> _scans = new();
    private readonly List<TrackedScan> _finished = new(); // gone scanners keep their stats for the summary
    private readonly HashSet<string> _gamesPresent = new(StringComparer.OrdinalIgnoreCase);
    private string? _sessionFile;
    private DateTimeOffset? _sessionStart;
    private DateTimeOffset? _lastScanSeen;
    private DateTimeOffset? _gameExitAt;
    private DateTimeOffset? _gameStartAt;
    private bool _fgFullscreen;
    private double _fgBusySeconds;
    private double _fgTotalSeconds;
    private DateTimeOffset? _fgLastTick;
    private bool _elevated;

    public static readonly string[] GameNames = { "VALORANT-Win64-Shipping", "Endfield" };

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mvxxcb-restrain", "sessions");

    private void EnsureSession(DateTimeOffset now)
    {
        if (_sessionFile is not null) return;
        lock (_gate)
        {
            if (_sessionFile is not null) return;
            _sessionStart = now;
            try
            {
                Directory.CreateDirectory(Dir);
                _sessionFile = Path.Combine(Dir, $"session-{now:yyyyMMdd-HHmmss}.jsonl");
                File.AppendAllText(_sessionFile, J(now, "session-start", $"elevated={_elevated}") + Environment.NewLine);
            }
            catch { _sessionFile = null; }
        }
    }

    private static string J(DateTimeOffset t, string evt, string detail = "") =>
        "{\"ts\":\"" + t.ToString("o") + "\",\"evt\":\"" + evt + "\",\"detail\":\"" + detail.Replace("\"", "'") + "\"}";

    private void Append(string evt, string detail)
    {
        try
        {
            string? file;
            lock (_gate) file = _sessionFile;
            if (file is not null)
                File.AppendAllText(file, J(DateTimeOffset.Now, evt, detail) + Environment.NewLine);
        }
        catch { }
    }

    public void ScannerFound(int pid, string name, DateTimeOffset now)
    {
        EnsureSession(now);
        lock (_gate)
        {
            _scans[pid] = new TrackedScan { Name = name, FirstSeen = now, LastSeen = now };
            _lastScanSeen = now;
        }
        Append("scanner-found", $"pid={pid} name={name}");
    }

    public void ScannerGone(int pid, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_scans.TryGetValue(pid, out var s))
            {
                _finished.Add(s);
                if (_finished.Count > 20) _finished.RemoveAt(0);
                _scans.Remove(pid);
            }
            _lastScanSeen = now;
        }
        Append("scanner-gone", $"pid={pid}");
    }

    /// <summary>Feed engine samples: rate, state, real dt of the sample window.</summary>
    public void EngineSample(int pid, double mbps, string state, DateTimeOffset now, double dt)
    {
        bool logRate = false;
        lock (_gate)
        {
            if (!_scans.TryGetValue(pid, out var s)) return;
            s.LastSeen = now;
            _lastScanSeen = now;
            s.Samples++;
            if (dt > 0 && dt < 10)
            {
                s.EstReadMB += mbps * dt;
                if (state == "Throttled") s.ThrottledSeconds += dt;
            }
            if (mbps > s.PeakMBps) s.PeakMBps = mbps;
            // persist rates: every burst sample, else a 10s heartbeat per scanner
            if (mbps >= 5 || (now - s.LastRateLog).TotalSeconds >= 10)
            {
                s.LastRateLog = now;
                logRate = true;
            }
        }
        if (logRate) Append("rate", $"pid={pid} mbps={mbps:F1} state={state}");
    }

    /// <summary>Sampler could not read this pid (denied or gone) - the observability signal.</summary>
    public void SampleDenied(int pid, DateTimeOffset now)
    {
        bool log = false;
        lock (_gate)
        {
            if (!_scans.TryGetValue(pid, out var s)) return;
            s.DeniedSamples++;
            if ((now - s.LastDenyLog).TotalSeconds >= 30)
            {
                s.LastDenyLog = now;
                log = true;
            }
        }
        if (log) Append("observe-denied", $"pid={pid}");
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

    /// <summary>Feed the foreground-fullscreen signal each worker tick (dt in seconds).</summary>
    public void Foreground(bool fullscreen, DateTimeOffset now)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_fgLastTick.HasValue)
            {
                var d = (now - _fgLastTick.Value).TotalSeconds;
                if (d > 0 && d < 10)
                {
                    _fgTotalSeconds += d;
                    if (_fgFullscreen) _fgBusySeconds += d;
                }
            }
            _fgLastTick = now;
            changed = fullscreen != _fgFullscreen;
            _fgFullscreen = fullscreen;
        }
        if (changed) Append(fullscreen ? "fg-fullscreen-enter" : "fg-fullscreen-exit", "");
    }

    /// <summary>Poll annotated game executables; called every ~1s from the worker.</summary>
    public void PollGames(DateTimeOffset now)
    {
        foreach (var name in GameNames)
        {
            bool present;
            try { present = System.Diagnostics.Process.GetProcessesByName(name).Length > 0; }
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
                if (!present && _gamesPresent.Count == 0 && _scans.Count > 0)
                {
                    _gameExitAt = now;
                    foreach (var s in _scans.Values)
                    {
                        s.EstReadMBAtGameExit = s.EstReadMB;
                        s.MarkedGameExit = true;
                    }
                }
            }
        }
        if (changed) Append(present ? "game-start" : "game-exit", name);
    }

    /// <summary>Close the session once scanners have been quiet for 2 minutes; writes the summary.</summary>
    public void TickCloseCheck(DateTimeOffset now)
    {
        bool open; DateTimeOffset? lastSeen;
        lock (_gate) { open = _sessionFile is not null; lastSeen = _lastScanSeen; }
        if (!open) return;
        lock (_gate) { if (_scans.Count > 0) return; }
        if (lastSeen.HasValue && (now - lastSeen.Value).TotalMinutes < 2) return;
        WriteSummary(now);
        lock (_gate)
        {
            _sessionFile = null; _sessionStart = null; _scans.Clear();
            _lastScanSeen = null; _gameExitAt = null; _gameStartAt = null;
            _fgLastTick = null; _fgBusySeconds = 0; _fgTotalSeconds = 0; _finished.Clear();
        }
    }

    public void ForceClose(string reason)
    {
        bool open;
        lock (_gate) open = _sessionFile is not null;
        if (!open) return;
        WriteSummary(DateTimeOffset.Now, reason);
        lock (_gate)
        {
            _sessionFile = null; _sessionStart = null; _scans.Clear();
            _lastScanSeen = null; _gameExitAt = null; _gameStartAt = null;
            _fgLastTick = null; _fgBusySeconds = 0; _fgTotalSeconds = 0; _finished.Clear();
        }
    }

    private void WriteSummary(DateTimeOffset now, string reason = "scanner quiet for 2 minutes")
    {
        TrackedScan[] scans; DateTimeOffset? start, gameExit, gameStart; DateTimeOffset? lastSeen;
        double fgBusy, fgTotal; bool elevated;
        lock (_gate)
        {
            scans = _scans.Values.Concat(_finished).ToArray();
            start = _sessionStart; gameExit = _gameExitAt; gameStart = _gameStartAt; lastSeen = _lastScanSeen;
            fgBusy = _fgBusySeconds; fgTotal = _fgTotalSeconds; elevated = _elevated;
        }
        var sb = new StringBuilder();
        sb.AppendLine("==================================================");
        sb.AppendLine($"mvxxcb-restrain 监测报告  {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"运行权限: {(elevated ? "已提权（可观测/调度系统级进程）" : "普通（未提权，系统级扫描进程可能无法观测——请运行 register-task.ps1）")}");
        sb.AppendLine($"会话开始: {start:HH:mm:ss}   结束: {now:HH:mm:ss}   关闭原因: {reason}");
        if (gameStart.HasValue) sb.AppendLine($"游戏运行: 自 {gameStart:HH:mm:ss}" + (gameExit.HasValue ? $" 至 {gameExit:HH:mm:ss}" : "（仍在运行）"));
        if (gameExit.HasValue && lastSeen.HasValue)
        {
            var tail = lastSeen.Value - gameExit.Value;
            var tailReadGB = scans.Where(s => s.MarkedGameExit).Sum(s => (s.EstReadMB - s.EstReadMBAtGameExit)) / 1024;
            sb.AppendLine($"★ 游戏退出后，扫描进程仍活跃了约 {tail.TotalMinutes:F1} 分钟，期间估算读取 {tailReadGB:F2} GB");
        }
        else if (scans.Length == 0 && gameExit.HasValue)
        {
            sb.AppendLine("游戏退出后未观察到扫描进程活动");
        }
        if (fgTotal > 10)
            sb.AppendLine($"前台全屏时间占比: {fgBusy / fgTotal:P0}（{fgBusy / 60:F0}/{fgTotal / 60:F0} 分钟）");
        sb.AppendLine($"本次调度: 限速施加 {scans.Sum(s => s.Throttles)} 次（累计 {scans.Sum(s => s.ThrottledSeconds) / 60:F1} 分钟）, 解除 {scans.Sum(s => s.Releases)} 次, 空闲放行 {scans.Sum(s => s.Passthroughs)} 次");
        foreach (var s in scans)
        {
            sb.AppendLine($"  - {s.Name}: 活跃 {s.FirstSeen:HH:mm:ss}→{s.LastSeen:HH:mm:ss}, 估算读取 {s.EstReadMB / 1024:F2} GB, 峰值 {s.PeakMBps:F0} MB/s, 采样 {s.Samples} 次/被拒 {s.DeniedSamples} 次");
        }
        sb.AppendLine($"磁盘状态: {DiskStatusLine()}");
        sb.AppendLine($"明细时间线: {Path.GetFileName(VolatileFile)}");
        sb.AppendLine();
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "summary.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
        Append("session-summary", "written to summary.txt");
    }

    private string? VolatileFile { get { lock (_gate) return _sessionFile; } }

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

    public void SetElevated(bool elevated)
    {
        lock (_gate) _elevated = elevated;
    }
}
