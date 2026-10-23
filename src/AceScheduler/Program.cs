using System.Diagnostics;
using AceScheduler;

if (args.Length > 0 && args[0] == "--selftest")
{
    return SelfTest.Run(args);
}

var options = new SchedulerOptions();
var fullMask = ProcessThrottle.FullAffinityMask();
var targets = new Dictionary<int, TargetEntry>();

void LogEvent(int pid, SchedulerEvent ev)
    => System.Diagnostics.Debug.WriteLine($"[{ev.Timestamp:HH:mm:ss}] pid={pid} {ev.Kind} {ev.RateMBps:F0}MB/s {ev.Detail}");

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
        targets.Remove(pid);
    }

    foreach (var proc in TargetDiscovery.FindScanners())
    {
        if (targets.ContainsKey(proc.Id))
        {
            proc.Dispose();
            continue;
        }
        IoSampler.GetCumulativeReadBytes(proc.Id); // prime the sampler before the engine needs it
        var engine = new SchedulerStateMachine(
            options,
            throttle => ProcessThrottle.Apply(proc, throttle, fullMask),
            ev => LogEvent(proc.Id, ev));
        targets[proc.Id] = new TargetEntry { Proc = proc, Engine = engine };
    }
}

void Tick(object? sender, EventArgs e)
{
    RetargetIfNeeded();
    if (targets.Count == 0) return;
    var fg = ForegroundMonitor.IsFullscreenApp();
    var now = DateTimeOffset.UtcNow;
    foreach (var entry in targets.Values)
    {
        var bytes = IoSampler.GetCumulativeReadBytes(entry.Proc.Id);
        if (bytes.HasValue) entry.Engine.OnSample(bytes.Value, now, fg);
    }
}

using var timer = new System.Windows.Forms.Timer { Interval = 500 };
timer.Tick += Tick;
timer.Start();

var menu = new ContextMenuStrip();
menu.Items.Add($"mvxxcb-restrain v0.3 ({options.BurstMBs:F0}MB/s threshold)", null, (_, _) => { });
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("Exit", null, (_, _) => Application.Exit());

System.Drawing.Icon trayImage = SystemIcons.Shield;
try
{
    if (Environment.ProcessPath is not null)
        trayImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? trayImage;
}
catch { }

var notifyIcon = new NotifyIcon
{
    Icon = trayImage,
    Text = "mvxxcb-restrain - scheduling background scans",
    ContextMenuStrip = menu,
    Visible = true,
};

Application.ApplicationExit += (_, _) => notifyIcon.Dispose();
Application.Run();
return 0;

sealed class TargetEntry : IDisposable
{
    public required Process Proc;
    public required SchedulerStateMachine Engine;
    public DateTimeOffset LastTs;
    public double LastBytes;

    public void Dispose()
    {
        try { Proc.Dispose(); } catch { }
    }
}
