using System.Diagnostics;
using MvxxcbRestrain;

if (args.Length > 0 && args[0] == "--selftest")
{
    return SelfTest.Run(args);
}

// --- single instance: a second launch only activates the first one's panel ---
using var single = new Mutex(true, "mvxxcb-restrain-single-instance", out bool firstInstance);
if (!firstInstance)
{
    try { EventWaitHandle.OpenExisting("mvxxcb-restrain-activate").Set(); } catch { }
    return 0;
}
using var activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "mvxxcb-restrain-activate");

var options = new SchedulerOptions();
var fullMask = ProcessThrottle.FullAffinityMask();
var gate = new object();                     // guards targets + shared reads
var targets = new Dictionary<int, TargetEntry>();
var goneUntil = new Dictionary<int, DateTimeOffset>(); // re-adoption cooldown (anti-flap)
var monitor = new SessionMonitor();
var shared = new SharedState();
NotifyIcon? notifyIcon = null;
Form? panel = null;
Label statusLabel = null!;
Button toggleButton = null!;
ToolStripMenuItem toggleItem = null!;

bool elevated = SessionMonitor.IsElevated();
monitor.SetElevated(elevated);
FileLogger.Write($"running elevated: {elevated}");

// config directory + foreground identity list (user-editable, ships empty)
var configDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "mvxxcb-restrain");
var foregroundListPath = Path.Combine(configDir, "foreground-processes.txt");
ForegroundMonitor.Configure(foregroundListPath);
SessionMonitor.ConfigureGames(Path.Combine(configDir, "games.txt"));
ProcessThrottle.Configure(Path.Combine(configDir, "throttle-journal.json"));
try
{
    Directory.CreateDirectory(configDir);
    if (!File.Exists(foregroundListPath))
    {
        File.WriteAllText(foregroundListPath,
            """
            # 前台进程清单:每行一个进程名。这些进程位于前台时,扫描会被限速让路
            # 可带或不带 .exe 后缀;修改保存后立即生效,无需重启
            # 示例(去掉行首 # 生效):
            # notepad.exe
            """);
    }
    var gamesListPath = Path.Combine(configDir, "games.txt");
    if (!File.Exists(gamesListPath))
    {
        File.WriteAllText(gamesListPath,
            """
            # 游戏进程清单:每行一个游戏主程序进程名,用于会话监测把扫描活动与游戏时段对齐
            # 可带或不带 .exe 后缀;修改保存后立即生效,无需重启
            # 示例(去掉行首 # 生效):
            # somegame
            """);
    }
}
catch { }
try
{
    // crash recovery: restore anything the previous run left throttled.
    // Transactional: entries that cannot be restored yet (e.g. access
    // denied against a protected target while non-elevated) stay journaled
    // and are retried on the next start; invalid entries are dropped loudly.
    var (kept, restored, droppedInvalid) = ProcessThrottle.RecoverAtStartup();
    if (restored > 0 || kept > 0 || droppedInvalid > 0)
        FileLogger.Write($"startup journal recovery: restored={restored}, kept-for-retry={kept}, dropped-invalid={droppedInvalid}");
}
catch { }

// persisted on/off state: the launcher remembers the user's last choice
try
{
    var stateFile = Path.Combine(configDir, "enabled.txt");
    if (File.Exists(stateFile) && File.ReadAllText(stateFile).Trim() == "0") shared.Enabled = false;
}
catch { }

void PersistEnabled()
{
    try
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "enabled.txt"), shared.Enabled ? "1" : "0");
    }
    catch { }
}

void LogEvent(int pid, SchedulerEvent ev)
{
    var line = $"pid={pid} {ev.Kind} {ev.RateMBps:F0}MB/s {ev.Detail}";
    FileLogger.Write(line);
    monitor.EngineEvent(pid, ev.Kind, ev.Detail);
}

const int NullStreakLimit = 10;      // ~5s of unreadable IO counters = gone
const double ReadoptCooldownSec = 15; // same pid must stay gone this long before re-adoption
const double DeniedCooldownSec = 600; // never successfully sampled = observation denied; long backoff

/// <summary>
/// Liveness is sampler-based: a target is dead only when its IO counters are
/// unreadable for NullStreakLimit consecutive ticks. We never hold Process
/// objects - Refresh()/HasExited throw access-denied for SYSTEM-owned scanner
/// processes and previously caused 582 adopt/drop flaps per hour.
/// NullStreak alone is not trusted (external review P0-1): a transient WMI
/// outage also produces nulls. At the limit we cross-check with
/// Process.GetProcessById - a lightweight existence query that works for
/// SYSTEM processes. Missing = dead (release first, then forget); still
/// present = sampler outage, keep the target AND its throttle snapshot.
/// A target that NEVER produced a readable sample is treated as
/// observation-denied (e.g. non-elevated vs SYSTEM) and backs off for
/// DeniedCooldownSec instead of cycling adopt/drop every ~20s forever.
/// </summary>
void RetargetIfNeeded(DateTimeOffset now)
{
    lock (gate)
    {
        // expired cooldowns leave; the dictionary must not grow forever with
        // scanner pid churn (one game session can cycle dozens of pids)
        if (goneUntil.Count > 0)
        {
            foreach (var stale in goneUntil.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList())
            {
                goneUntil.Remove(stale);
            }
        }

        var dead = new List<int>();
        foreach (var (pid, entry) in targets)
        {
            if (entry.NullStreak < NullStreakLimit) continue;
            var alive = false;
            var sameIdentity = true;
            try
            {
                var p = Process.GetProcessById(pid);
                alive = true;
                // identity check: pid reuse must not pass the liveness probe
                if (entry.CreatedAtFiletime.HasValue)
                {
                    try
                    {
                        if (p.StartTime.ToFileTime() != entry.CreatedAtFiletime.Value) sameIdentity = false;
                    }
                    catch { /* start time unreadable: keep the conservative verdict */ }
                }
            }
            catch (ArgumentException) { }
            if (alive && sameIdentity)
            {
                // sampler outage on a live target: keep target + snapshot,
                // keep any throttle as-is; log at most once a minute
                entry.NullStreak = 0;
                if ((now - entry.LastObserveLostLog).TotalSeconds > 60)
                {
                    entry.LastObserveLostLog = now;
                    FileLogger.Write($"pid={pid} samples lost but process is alive (sampler outage) - target and throttle state kept");
                }
                continue;
            }
            if (alive && !sameIdentity)
            {
                // the pid now belongs to someone else: treat the old target as dead
                FileLogger.Write($"pid={pid} was reused by a different process - treating old target as dead");
            }
            dead.Add(pid);
        }
        foreach (var pid in dead)
        {
            var everSampled = targets[pid].EverSampled;
            var name = targets[pid].Name;
            targets.Remove(pid);
            // Best-effort release; only a confirmed restore or confirmed death
            // discards the snapshot - access-denied/partial failures stay
            // journaled for the next startup (review P0-B/P1-5).
            var (outcome, err) = ProcessThrottle.TryRecoverBeforeForget(pid, name, fullMask);
            if (err is not null || outcome != ProcessThrottle.RecoverOutcome.Gone && outcome != ProcessThrottle.RecoverOutcome.Restored)
                FileLogger.Write($"pid={pid} final recover: outcome={outcome}" + (err is null ? "" : $", {err}"));
            goneUntil[pid] = now.AddSeconds(everSampled ? ReadoptCooldownSec : DeniedCooldownSec);
            if (!everSampled)
                FileLogger.Write($"pid={pid} was never observable (access denied?) - re-adoption backed off {DeniedCooldownSec:F0}s; run elevated to observe SYSTEM scanners");
            monitor.ScannerGone(pid, now);
        }

        foreach (var proc in TargetDiscovery.FindScanners())
        {
            var pid = proc.Id;
            var name = proc.ProcessName;
            long? createdAt = null;
            try { createdAt = proc.StartTime.ToFileTime(); } catch { }
            proc.Dispose(); // read what we need, drop the handle immediately
            if (targets.ContainsKey(pid)) continue;
            if (goneUntil.TryGetValue(pid, out var until) && now < until) continue;

            var entry = new TargetEntry { Pid = pid, Name = name, CreatedAtFiletime = createdAt };
            entry.Engine = new SchedulerStateMachine(
                options,
                throttle => ProcessThrottle.Apply(pid, name, throttle, fullMask),
                ev => LogEvent(pid, ev));
            targets[pid] = entry;
            monitor.ScannerFound(pid, name, now);
            FileLogger.Write($"target adopted: pid={pid} name={name}");
        }
    }
}

(string Status, string Tooltip) StatusStrings()
{
    int count; string? err;
    lock (gate)
    {
        count = targets.Count;
        err = targets.Values.FirstOrDefault(t => t.Engine.LastError is not null)?.Engine.LastError;
    }
    var elevTag = elevated ? "" : " | 未提权";
    if (!shared.Enabled) return ("当前状态：已停用（不干预任何进程）", "mvxxcb-restrain | 已停用" + elevTag);
    if (count == 0) return ("当前状态：运行中，未发现扫描进程", "mvxxcb-restrain | 运行中，未发现扫描进程" + elevTag);
    if (err is not null)
    {
        var s = $"当前状态：限速失败（{err}）";
        return (s, Trim63("mvxxcb-restrain | " + s[5..] + elevTag));
    }
    var t = $"当前状态：调度中，目标进程 {count} 个";
    return (t, Trim63("mvxxcb-restrain | " + t[5..] + elevTag));
}

static string Trim63(string s) => s.Length > 63 ? s[..63] : s;

/// <summary>Marshal UI updates onto the UI thread; safe no-op during shutdown.</summary>
void RunOnUi(Action action)
{
    try
    {
        if (panel is null || !panel.IsHandleCreated || panel.IsDisposed) return;
        if (panel.InvokeRequired) panel.BeginInvoke(action);
        else action();
    }
    catch { /* UI gone - shutting down */ }
}

void ApplyUi((string Status, string Tooltip) s)
{
    notifyIcon!.Text = Trim63(s.Tooltip);
    statusLabel.Text = s.Status;
    toggleButton.Text = shared.Enabled ? "停用（OFF）" : "启用（ON）";
    toggleItem.Text = shared.Enabled ? "停用调度" : "启用调度";
}

void UpdateUiFromWorker() => RunOnUi(() => ApplyUi(StatusStrings()));

/// <summary>
/// Release every throttle we hold. Runs on the worker at the top of a tick
/// after the user disabled scheduling (never inline on the UI thread: the
/// UI click must not block on WMI-era locks, and ordering after any
/// in-flight tick is exactly what kills the disable race).
/// </summary>
void DoReleaseAll()
{
    foreach (var pid in targets.Keys.ToList())
    {
        var name = targets[pid].Name;
        var err = ProcessThrottle.Apply(pid, name, false, fullMask);
        // keep the snapshot only when restore PARTIALLY failed - the journal
        // then survives for retry/next-startup recovery. Full success removes
        // it inside Apply.
        FileLogger.Write(err is null ? $"released pid={pid}" : $"release pid={pid} failed: {err}");
    }
}

void SetEnabled(bool enabled)
{
    // flip the flag only; the worker performs the actual release on its next
    // tick under the same lock that guards sampling - after any in-flight
    // tick completes - so a late re-throttle cannot survive the disable
    lock (gate) shared.Enabled = enabled;
    PersistEnabled();
    FileLogger.Write(enabled ? "scheduler enabled by user" : "scheduler disabled by user");
    monitor.Log("scheduler", enabled ? "enabled" : "disabled");
    ApplyUi(StatusStrings()); // caller is on the UI thread
}

// ---------------- background worker: all discovery/sampling off the UI thread ----------------
void WorkerLoop()
{
    var lastTick = DateTimeOffset.UtcNow;
    var lastTrim = DateTimeOffset.UtcNow - MemoryMaintenance.TrimInterval; // first trim soon after startup
    while (true)
    {
        Thread.Sleep(500);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var dt = (now - lastTick).TotalSeconds;
            lastTick = now;

            // resident-process housekeeping: compact the heap and drop the
            // working set back so a days-long tray session does not creep up
            if (now - lastTrim >= MemoryMaintenance.TrimInterval)
            {
                var before = MemoryMaintenance.WorkingSetMB();
                MemoryMaintenance.Trim();
                var after = MemoryMaintenance.WorkingSetMB();
                if (before >= 0 && after >= 0 && before - after >= 5)
                    FileLogger.Write($"memory trimmed: working set {before}MB -> {after}MB");
                lastTrim = now;
            }

            var fg = ForegroundMonitor.IsForegroundBusy();
            monitor.Foreground(fg, now);
            monitor.PollGames(now);
            monitor.TickCloseCheck(now);

            if (shared.Enabled)
            {
                RetargetIfNeeded(now);
                lock (gate)
                {
                    foreach (var entry in targets.Values)
                    {
                        var bytes = IoSampler.GetCumulativeReadBytes(entry.Pid);
                        if (bytes is null)
                        {
                            entry.NullStreak++;
                            monitor.SampleDenied(entry.Pid, now);
                            continue;
                        }
                        entry.NullStreak = 0;
                        entry.EverSampled = true;
                        // periodic identity probe (every ~2s): a reused pid with
                        // readable IO never trips NullStreak, so the identity
                        // must be checked on the happy path too (review P0-D)
                        if (entry.CreatedAtFiletime.HasValue && ++entry.IdentityTick >= 4)
                        {
                            entry.IdentityTick = 0;
                            try
                            {
                                using var p = Process.GetProcessById(entry.Pid);
                                if (p.StartTime.ToFileTime() != entry.CreatedAtFiletime.Value)
                                {
                                    FileLogger.Write($"pid={entry.Pid} identity changed (pid reused) - retiring target");
                                    entry.NullStreak = NullStreakLimit; // next RetargetIfNeeded run takes the dead path
                                }
                            }
                            catch { /* unreadable now: retry on the next probe */ }
                        }
                        var result = entry.Engine.OnSample(bytes.Value, now, fg);
                        monitor.EngineSample(entry.Pid, result.RateMBps, result.State.ToString(), now, dt);
                    }
                }
            }
            else
            {
                // the disable raced nothing: this runs after any in-flight
                // tick, under the same lock, so a late re-throttle cannot
                // survive. Release once, then drop tracking - re-enabling
                // re-adopts. The monitor is told so the session timeline and
                // summary see the targets go instead of waiting for exit.
                lock (gate)
                {
                    if (targets.Count > 0)
                    {
                        DoReleaseAll();
                        foreach (var pid in targets.Keys)
                        {
                            monitor.ScannerGone(pid, now);
                        }
                        targets.Clear();
                    }
                }
            }
            UpdateUiFromWorker();
        }
        catch (Exception ex)
        {
            FileLogger.Write("worker error: " + ex.Message);
        }
    }
}

// ---------------- UI ----------------
System.Drawing.Icon trayImage = SystemIcons.Shield;
try
{
    if (Environment.ProcessPath is not null)
        trayImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? trayImage;
}
catch { }

panel = new Form
{
    Text = "mvxxcb-restrain 控制面板",
    FormBorderStyle = FormBorderStyle.FixedDialog,
    MaximizeBox = false,
    MinimizeBox = false,
    StartPosition = FormStartPosition.CenterScreen,
    ClientSize = new System.Drawing.Size(360, 170),
    ShowInTaskbar = false,
};
statusLabel = new Label
{
    Text = "当前状态：…",
    AutoSize = false,
    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
    Location = new System.Drawing.Point(16, 16),
    Size = new System.Drawing.Size(328, 30),
};
var elevLabel = new Label
{
    Text = elevated ? "运行权限：已提权" : "运行权限：普通（未提权，系统级扫描进程可能无法观测，详见日志）",
    AutoSize = false,
    ForeColor = elevated ? System.Drawing.Color.Green : System.Drawing.Color.DarkOrange,
    Location = new System.Drawing.Point(16, 48),
    Size = new System.Drawing.Size(328, 30),
};
toggleButton = new Button
{
    Text = "…",
    Location = new System.Drawing.Point(16, 88),
    Size = new System.Drawing.Size(218, 45),
};
toggleButton.Click += (_, _) => SetEnabled(!shared.Enabled);
var feedbackButton = new Button
{
    Text = "反馈数据",
    Location = new System.Drawing.Point(242, 88),
    Size = new System.Drawing.Size(102, 45),
};
feedbackButton.Click += (_, _) =>
{
    try
    {
        var zip = FeedbackExport.Build();
        FileLogger.Write("feedback bundle created: " + zip);
        MessageBox.Show(panel, "已打包到桌面：\n" + zip, "反馈数据",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    catch (Exception ex)
    {
        MessageBox.Show(panel, "打包失败：" + ex.Message, "反馈数据",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
};
panel.Controls.Add(statusLabel);
panel.Controls.Add(elevLabel);
panel.Controls.Add(toggleButton);
panel.Controls.Add(feedbackButton);
panel.FormClosing += (_, e) =>
{
    // closing the window hides it; the app keeps running in the tray
    if (e.CloseReason == CloseReason.UserClosing)
    {
        e.Cancel = true;
        panel.Hide();
    }
};
_ = panel.Handle; // force handle creation now so worker marshaling works immediately

var menu = new ContextMenuStrip();
menu.Items.Add("打开控制面板", null, (_, _) => { panel.Show(); panel.Activate(); });
toggleItem = new ToolStripMenuItem("…");
toggleItem.Click += (_, _) => SetEnabled(!shared.Enabled);
menu.Items.Add(toggleItem);
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("打开监测报告", null, (_, _) =>
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mvxxcb-restrain", "sessions");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
    catch { }
});
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("打开日志", null, (_, _) =>
{
    try { Process.Start(new ProcessStartInfo(FileLogger.LogPath) { UseShellExecute = true }); } catch { }
});
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("退出", null, (_, _) =>
{
    // synchronous release on exit: the worker may never get another tick.
    // Anything that partially fails stays journaled and is recovered on the
    // next startup.
    lock (gate) shared.Enabled = false;
    lock (gate) DoReleaseAll();
    monitor.ForceClose("app exit");
    if (notifyIcon is not null) notifyIcon.Visible = false;
    Application.Exit();
});

notifyIcon = new NotifyIcon
{
    Icon = trayImage,
    Text = "mvxxcb-restrain",
    ContextMenuStrip = menu,
    Visible = true,
};
notifyIcon.DoubleClick += (_, _) => { panel.Show(); panel.Activate(); };

Application.ApplicationExit += (_, _) => { notifyIcon.Dispose(); panel.Dispose(); };

// second-instance activation: show the panel on demand
var activator = new Thread(() =>
{
    while (true)
    {
        activateSignal.WaitOne();
        RunOnUi(() => { panel.Show(); panel.Activate(); });
    }
})
{ IsBackground = true, Name = "activate-waiter" };
activator.Start();

var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "scheduler-worker" };
worker.Start();

ApplyUi(StatusStrings());
Application.Run();
return 0;

sealed class SharedState
{
    public volatile bool Enabled = true;
}

/// <summary>No Process object held: liveness is sampler-based (NullStreak).</summary>
sealed class TargetEntry
{
    public required int Pid;
    public required string Name;
    public SchedulerStateMachine Engine = null!;
    public int NullStreak;
    public bool EverSampled; // false at death = observation-denied -> long re-adoption backoff
    public DateTimeOffset LastObserveLostLog = DateTimeOffset.MinValue; // rate-limits sampler-outage logs
    public long? CreatedAtFiletime; // pid-reuse guard: the adopted process's identity
    public int IdentityTick; // periodic identity probe cadence (~4 samples = 2s)
}
