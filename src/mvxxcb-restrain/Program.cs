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
/// A target that NEVER produced a readable sample is treated as
/// observation-denied (e.g. non-elevated vs SYSTEM) and backs off for
/// DeniedCooldownSec instead of cycling adopt/drop every ~20s forever.
/// </summary>
void RetargetIfNeeded(DateTimeOffset now)
{
    lock (gate)
    {
        var dead = new List<int>();
        foreach (var (pid, entry) in targets)
        {
            if (entry.NullStreak >= NullStreakLimit) dead.Add(pid);
        }
        foreach (var pid in dead)
        {
            var everSampled = targets[pid].EverSampled;
            targets.Remove(pid);
            // Drop any throttle snapshot we hold for this pid: the process is
            // gone, restore is impossible, and a stale snapshot would pollute
            // the next innocent process that reuses this pid.
            if (ProcessThrottle.ForgetIfPresent(pid, out var wasThrottled) && wasThrottled)
                FileLogger.Write($"pid={pid} died while throttled; snapshot dropped (restore impossible)");
            goneUntil[pid] = now.AddSeconds(everSampled ? ReadoptCooldownSec : DeniedCooldownSec);
            if (!everSampled)
                FileLogger.Write($"pid={pid} was never observable (access denied?) - re-adoption backed off {DeniedCooldownSec:F0}s; run elevated to observe SYSTEM scanners");
            monitor.ScannerGone(pid, now);
        }

        foreach (var proc in TargetDiscovery.FindScanners())
        {
            var pid = proc.Id;
            var name = proc.ProcessName;
            proc.Dispose(); // read what we need, drop the handle immediately
            if (targets.ContainsKey(pid)) continue;
            if (goneUntil.TryGetValue(pid, out var until) && now < until) continue;

            var entry = new TargetEntry { Pid = pid, Name = name };
            entry.Engine = new SchedulerStateMachine(
                options,
                throttle => ProcessThrottle.Apply(pid, throttle, fullMask),
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

void SetEnabled(bool enabled)
{
    lock (gate)
    {
        shared.Enabled = enabled;
        if (!enabled)
        {
            // stop sampling AND release every throttle we currently hold
            foreach (var pid in targets.Keys.ToList())
            {
                var err = ProcessThrottle.Apply(pid, false, fullMask);
                ProcessThrottle.Forget(pid);
                FileLogger.Write(err is null ? $"released pid={pid}" : $"release pid={pid} failed: {err}");
            }
        }
    }
    PersistEnabled();
    FileLogger.Write(enabled ? "scheduler enabled by user" : "scheduler disabled by user");
    monitor.Log("scheduler", enabled ? "enabled" : "disabled");
    ApplyUi(StatusStrings()); // caller is on the UI thread
}

// ---------------- background worker: all discovery/sampling off the UI thread ----------------
void WorkerLoop()
{
    var lastTick = DateTimeOffset.UtcNow;
    while (true)
    {
        Thread.Sleep(500);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var dt = (now - lastTick).TotalSeconds;
            lastTick = now;

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
                        var result = entry.Engine.OnSample(bytes.Value, now, fg);
                        monitor.EngineSample(entry.Pid, result.RateMBps, result.State.ToString(), now, dt);
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
    Size = new System.Drawing.Size(328, 45),
};
toggleButton.Click += (_, _) => SetEnabled(!shared.Enabled);
panel.Controls.Add(statusLabel);
panel.Controls.Add(elevLabel);
panel.Controls.Add(toggleButton);
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
    SetEnabled(false); // restore everything we throttled before leaving
    monitor.ForceClose("app exit");
    notifyIcon.Visible = false;
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
}
