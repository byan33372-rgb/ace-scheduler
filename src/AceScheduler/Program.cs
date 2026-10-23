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

void UpdateTooltip()
{
    if (notifyIcon is null) return;
    var err = targets.Values.FirstOrDefault(t => t.Engine.LastError is not null)?.Engine.LastError;
    var text = targets.Count == 0
        ? "mvxxcb-restrain | no scanner detected"
        : err is not null
            ? "mvxxcb-restrain | THROTTLE FAILED: " + err
            : $"mvxxcb-restrain | scheduling {targets.Count} scanner(s)";
    if (text.Length > 63) text = text[..63];
    if (notifyIcon.Text != text) notifyIcon.Text = text;
}

void Tick(object? sender, EventArgs e)
{
    RetargetIfNeeded();
    UpdateTooltip();
    if (targets.Count == 0) return;
    var fg = ForegroundMonitor.IsFullscreenApp();
    var now = DateTimeOffset.UtcNow;
    foreach (var entry in targets.Values)
    {
        var bytes = IoSampler.GetCumulativeReadBytes(entry.Proc.Id);
        if (bytes.HasValue) entry.Engine.OnSample(bytes.Value, now, fg);
    }
}

var menu = new ContextMenuStrip();
menu.Items.Add($"mvxxcb-restrain v0.3 (log: {FileLogger.LogPath})", null, (_, _) => { });
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("Exit", null, (_, _) => Application.Exit());

System.Drawing.Icon trayImage = SystemIcons.Shield;
try
{
    if (Environment.ProcessPath is not null)
        trayImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? trayImage;
}
catch { }

notifyIcon = new NotifyIcon
{
    Icon = trayImage,
    Text = "mvxxcb-restrain",
    ContextMenuStrip = menu,
    Visible = true,
};

Application.ApplicationExit += (_, _) => notifyIcon.Dispose();

using var timer = new System.Windows.Forms.Timer { Interval = 500 };
timer.Tick += Tick;
timer.Start();

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
