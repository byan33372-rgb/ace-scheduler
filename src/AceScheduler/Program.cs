using System.Diagnostics;
using AceScheduler;

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
var monitor = new SessionMonitor();
var shared = new SharedState();
NotifyIcon? notifyIcon = null;
Form? panel = null;
Label statusLabel = null!;
Button toggleButton = null!;
ToolStripMenuItem toggleItem = null!;

// persisted on/off state: the launcher remembers the user's last choice
try
{
    var stateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mvxxcb-restrain", "enabled.txt");
    if (File.Exists(stateFile) && File.ReadAllText(stateFile).Trim() == "0") shared.Enabled = false;
}
catch { }

void PersistEnabled()
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mvxxcb-restrain");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "enabled.txt"), shared.Enabled ? "1" : "0");
    }
    catch { }
}

void LogEvent(int pid, SchedulerEvent ev)
{
    var line = $"pid={pid} {ev.Kind} {ev.RateMBps:F0}MB/s {ev.Detail}";
    FileLogger.Write(line);
    monitor.EngineEvent(pid, ev.Kind, ev.Detail);
}

void RetargetIfNeeded()
{
    lock (gate)
    {
        var dead = new List<int>();
        foreach (var (pid, entry) in targets)
        {
            try
            {
                entry.Proc.Refresh();
                if (entry.Proc.HasExited) dead.Add(pid);
            }
            catch { dead.Add(pid); }
        }
        foreach (var pid in dead)
        {
            targets[pid].Dispose();
            ProcessThrottle.Forget(pid);
            targets.Remove(pid);
            monitor.ScannerGone(pid, DateTimeOffset.UtcNow);
        }

        foreach (var proc in TargetDiscovery.FindScanners())
        {
            if (targets.ContainsKey(proc.Id))
            {
                proc.Dispose();
                continue;
            }
            IoSampler.GetCumulativeReadBytes(proc.Id); // prime the sampler
            var engine = new SchedulerStateMachine(
                options,
                throttle => ProcessThrottle.Apply(proc.Id, throttle, fullMask),
                ev => LogEvent(proc.Id, ev));
            targets[proc.Id] = new TargetEntry { Proc = proc, Engine = engine };
            monitor.ScannerFound(proc.Id, proc.ProcessName, DateTimeOffset.UtcNow);
            FileLogger.Write($"target adopted: pid={proc.Id}");
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
    if (!shared.Enabled) return ("当前状态：已停用（不干预任何进程）", "mvxxcb-restrain | 已停用");
    if (count == 0) return ("当前状态：运行中，未发现扫描进程", "mvxxcb-restrain | 运行中，未发现扫描进程");
    if (err is not null)
    {
        var s = $"当前状态：限速失败（{err}）";
        return (s, Trim63("mvxxcb-restrain | " + s[5..]));
    }
    var t = $"当前状态：调度中，目标进程 {count} 个";
    return (t, "mvxxcb-restrain | " + t[5..]);
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
int tick = 0;
void WorkerLoop()
{
    while (true)
    {
        Thread.Sleep(500);
        try
        {
            var now = DateTimeOffset.UtcNow;
            monitor.PollGames(now);
            monitor.TickCloseCheck(now);
            if (shared.Enabled)
            {
                RetargetIfNeeded();
                tick++;
                if (targets.Count > 0)
                {
                    var fg = ForegroundMonitor.IsFullscreenApp();
                    lock (gate)
                    {
                        foreach (var entry in targets.Values)
                        {
                            var bytes = IoSampler.GetCumulativeReadBytes(entry.Proc.Id);
                            if (bytes.HasValue)
                            {
                                var result = entry.Engine.OnSample(bytes.Value, now, fg);
                                monitor.EngineSample(entry.Proc.Id, result.RateMBps, result.State.ToString(), now);
                            }
                        }
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
    ClientSize = new System.Drawing.Size(360, 150),
    ShowInTaskbar = false,
};
statusLabel = new Label
{
    Text = "当前状态：…",
    AutoSize = false,
    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
    Location = new System.Drawing.Point(16, 20),
    Size = new System.Drawing.Size(328, 40),
};
toggleButton = new Button
{
    Text = "…",
    Location = new System.Drawing.Point(16, 75),
    Size = new System.Drawing.Size(328, 45),
};
toggleButton.Click += (_, _) => SetEnabled(!shared.Enabled);
panel.Controls.Add(statusLabel);
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

sealed class TargetEntry : IDisposable
{
    public required Process Proc;
    public required SchedulerStateMachine Engine;

    public void Dispose()
    {
        try { Proc.Dispose(); } catch { }
    }
}
