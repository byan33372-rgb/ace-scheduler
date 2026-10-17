using System.Diagnostics;
using AceScheduler;

if (args.Length > 0 && args[0] == "--selftest")
{
    return SelfTest.Run(args);
}

// AceScheduler v0.1 production skeleton: tray + polling engine.
// Roadmap to the zero-residency design (SCM 7045 trigger, foreground
// conflict detection) lives in docs/design.md.

var options = new SchedulerOptions();
var fullMask = ProcessThrottle.FullAffinityMask();
Process? target = null;
SchedulerStateMachine? engine = null;

void LogEvent(SchedulerEvent ev)
    => System.Diagnostics.Debug.WriteLine($"[{ev.Timestamp:HH:mm:ss}] {ev.Kind} {ev.RateMBps:F0}MB/s {ev.Detail}");

void RetargetIfNeeded()
{
    if (target is not null && !target.HasExited) return;
    target?.Dispose();
    target = null;
    engine = null;
    var found = TargetDiscovery.FindScanner();
    if (found is null) return;
    target = found;
    IoSampler.GetCumulativeReadBytes(target.Id); // prime WMI before the engine needs it
    engine = new SchedulerStateMachine(
        options,
        throttle => ProcessThrottle.Apply(target, throttle, fullMask),
        LogEvent);
}

void Tick(object? sender, EventArgs e)
{
    RetargetIfNeeded();
    if (target is null || engine is null) return;
    var bytes = IoSampler.GetCumulativeReadBytes(target.Id);
    if (bytes.HasValue) engine.OnSample(bytes.Value, DateTimeOffset.UtcNow, ForegroundMonitor.IsFullscreenApp());
}

using var timer = new System.Windows.Forms.Timer { Interval = 500 };
timer.Tick += Tick;
timer.Start();

var menu = new ContextMenuStrip();
menu.Items.Add("AceScheduler v0.1 (skeleton)", null, (_, _) => { });
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add("Exit", null, (_, _) => Application.Exit());

var notifyIcon = new NotifyIcon
{
    Icon = SystemIcons.Shield,
    Text = "AceScheduler - scheduling ACE scans (skeleton)",
    ContextMenuStrip = menu,
    Visible = true,
};

Application.ApplicationExit += (_, _) => notifyIcon.Dispose();
Application.Run();
return 0;
