using System.Diagnostics;
using AceScheduler;

if (args.Length > 0 && args[0] == "--selftest")
{
    return SelfTest.Run(args);
}

var options = new SchedulerOptions();
var fullMask = ProcessThrottle.FullAffinityMask();
var targets = new Dictionary<int, TargetEntry>();
NotifyIcon? notifyIcon = null;
Form? panel = null;
Label statusLabel = null!;
Button toggleButton = null!;
System.Windows.Forms.Timer? timer = null;

// persisted on/off state: the launcher remembers the user's last choice
bool schedulerEnabled = true;
try
{
    var stateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mvxxcb-restrain", "enabled.txt");
    if (File.Exists(stateFile) && File.ReadAllText(stateFile).Trim() == "0") schedulerEnabled = false;
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
        File.WriteAllText(Path.Combine(dir, "enabled.txt"), schedulerEnabled ? "1" : "0");
    }
    catch { }
}

void LogEvent(int pid, SchedulerEvent ev)
{
    var line = $"pid={pid} {ev.Kind} {ev.RateMBps:F0}MB/s {ev.Detail}";
    Debug.WriteLine($"[{ev.Timestamp:HH:mm:ss}] {line}");
    FileLogger.Write(line);
}

void RetargetIfNeeded()
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
        FileLogger.Write($"target adopted: pid={proc.Id}");
    }
}

string StatusText()
{
    if (!schedulerEnabled) return "当前状态：已停用（不干预任何进程）";
    if (targets.Count == 0) return "当前状态：运行中，未发现扫描进程";
    var err = targets.Values.FirstOrDefault(t => t.Engine.LastError is not null)?.Engine.LastError;
    if (err is not null) return $"当前状态：限速失败（{err}）";
    return $"当前状态：调度中，目标进程 {targets.Count} 个";
}

void RefreshUi()
{
    var tooltip = schedulerEnabled
        ? "mvxxcb-restrain | " + StatusText()[5..]
        : "mvxxcb-restrain | 已停用";
    if (tooltip.Length > 63) tooltip = tooltip[..63];
    if (notifyIcon is not null && notifyIcon.Text != tooltip) notifyIcon.Text = tooltip;
    if (panel is not null)
    {
        statusLabel.Text = StatusText();
        toggleButton.Text = schedulerEnabled ? "停用（OFF）" : "启用（ON）";
    }
}

void SetEnabled(bool enabled)
{
    schedulerEnabled = enabled;
    PersistEnabled();
    FileLogger.Write(enabled ? "scheduler enabled by user" : "scheduler disabled by user");
    if (!enabled)
    {
        // stop sampling AND release every throttle we currently hold
        timer?.Stop();
        foreach (var pid in targets.Keys.ToList())
        {
            var err = ProcessThrottle.Apply(pid, false, fullMask);
            ProcessThrottle.Forget(pid);
            FileLogger.Write(err is null ? $"released pid={pid}" : $"release pid={pid} failed: {err}");
        }
    }
    else
    {
        timer?.Start();
    }
    RefreshUi();
}

void Tick(object? sender, EventArgs e)
{
    RetargetIfNeeded();
    if (targets.Count == 0) { RefreshUi(); return; }
    var fg = ForegroundMonitor.IsFullscreenApp();
    var now = DateTimeOffset.UtcNow;
    foreach (var entry in targets.Values)
    {
        var bytes = IoSampler.GetCumulativeReadBytes(entry.Proc.Id);
        if (bytes.HasValue) entry.Engine.OnSample(bytes.Value, now, fg);
    }
    RefreshUi();
}

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
    Text = StatusText(),
    AutoSize = false,
    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
    Location = new System.Drawing.Point(16, 20),
    Size = new System.Drawing.Size(328, 40),
};
toggleButton = new Button
{
    Text = schedulerEnabled ? "停用（OFF）" : "启用（ON）",
    Location = new System.Drawing.Point(16, 75),
    Size = new System.Drawing.Size(328, 45),
};
toggleButton.Click += (_, _) => SetEnabled(!schedulerEnabled);
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

var menu = new ContextMenuStrip();
menu.Items.Add("打开控制面板", null, (_, _) => { panel.Show(); panel.Activate(); });
var toggleItem = new ToolStripMenuItem();
void SyncToggleItem() => toggleItem.Text = schedulerEnabled ? "停用调度" : "启用调度";
toggleItem.Click += (_, _) => { SetEnabled(!schedulerEnabled); SyncToggleItem(); };
SyncToggleItem();
menu.Items.Add(toggleItem);
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("打开日志", null, (_, _) =>
{
    try { Process.Start(new ProcessStartInfo(FileLogger.LogPath) { UseShellExecute = true }); } catch { }
});
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("退出", null, (_, _) =>
{
    SetEnabled(false); // restore everything we throttled before leaving
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

timer = new System.Windows.Forms.Timer { Interval = 500 };
timer.Tick += Tick;
if (schedulerEnabled) timer.Start();

RefreshUi();
Application.Run();
return 0;

sealed class TargetEntry : IDisposable
{
    public required Process Proc;
    public required SchedulerStateMachine Engine;

    public void Dispose()
    {
        try { Proc.Dispose(); } catch { }
    }
}
