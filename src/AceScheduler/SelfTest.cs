using System.Diagnostics;
using System.Text.Json;

namespace AceScheduler;

/// <summary>
/// Headless mode for the sandbox closed-loop validation (v0.2 release gate).
/// Drives the production state machine against a caller-supplied target PID
/// under the exact same file contract as trial/AceScheduler.Trial.ps1:
/// a control file ('stop' exits) and a JSONL event log with matching event
/// names (start/sample/burst-detected/probe-start/probe-scanning/scan-ended/
/// target-gone/stop), so the trial orchestrator's verdict logic works
/// unchanged with this engine swapped in.
/// </summary>
public static class SelfTest
{
    private sealed record Evt(string ts, string evt, string? state, double? mbps, string? action);

    public static int Run(string[] args)
    {
        var targetPid = IntArg(args, "--target-pid", 0);
        if (targetPid == 0) return 2;
        var controlFile = StrArg(args, "--control", "limiter.ctl");
        var logFile = StrArg(args, "--log", "limiter.jsonl");
        var options = new SchedulerOptions
        {
            BurstMBs = DoubleArg(args, "--burst-mbs", 5),
            ProbeSeconds = DoubleArg(args, "--probe-sec", 5),
        };
        var pollMs = IntArg(args, "--poll-ms", 500);
        var foregroundFile = StrArg(args, "--foreground", "");

        Process target;
        try
        {
            target = Process.GetProcessById(targetPid);
        }
        catch
        {
            return 2;
        }

        var fullMask = ProcessThrottle.FullAffinityMask();

        void Log(Evt e)
        {
            try { File.AppendAllText(logFile, JsonSerializer.Serialize(e) + Environment.NewLine); }
            catch { /* logging must never kill the loop */ }
        }

        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("o");

        Log(new Evt(Utc(DateTimeOffset.UtcNow), "start", null, null,
            $"target={targetPid} burstMBs={options.BurstMBs} probeSec={options.ProbeSeconds}"));

        // warm up the WMI provider: the first query costs 1-2s and would widen
        // the unthrottled window before the engine engages
        IoSampler.GetCumulativeReadBytes(targetPid);

        var engine = new SchedulerStateMachine(
            options,
            throttle => ProcessThrottle.Apply(target, throttle, fullMask),
            ev =>
            {
                // map to the trial limiter's event names so orchestrator verdicts stay comparable
                var kind = ev.Kind switch
                {
                    "throttle" => "burst-detected",
                    "probe" => "probe-start",
                    "rethrottle" => "probe-scanning",
                    "release" => "scan-ended",
                    _ => ev.Kind,
                };
                Log(new Evt(Utc(ev.Timestamp), kind, null, ev.RateMBps, ev.Detail));
            });

        while (true)
        {
            Thread.Sleep(pollMs);
            if (ReadControl(controlFile) == "stop") break;
            try
            {
                target.Refresh();
                if (target.HasExited)
                {
                    Log(new Evt(Utc(DateTimeOffset.UtcNow), "target-gone", null, null, null));
                    break;
                }
            }
            catch { break; }

            var bytes = IoSampler.GetCumulativeReadBytes(target.Id);
            if (bytes is null) continue;
            var fgBusy = ReadControl(foregroundFile) != "idle"; // missing file / any other value = busy (legacy behavior)
            var result = engine.OnSample(bytes.Value, DateTimeOffset.UtcNow, fgBusy);
            Log(new Evt(Utc(DateTimeOffset.UtcNow), "sample", result.State.ToString(), result.RateMBps, null));
        }

        Log(new Evt(Utc(DateTimeOffset.UtcNow), "stop", engine.State.ToString(), null, null));
        try { target.Dispose(); } catch { }
        return 0;
    }

    private static string StrArg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static string? ReadControl(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    private static int IntArg(string[] args, string name, int fallback)
        => int.TryParse(StrArg(args, name, fallback.ToString()), out var v) ? v : fallback;

    private static double DoubleArg(string[] args, string name, double fallback)
        => double.TryParse(StrArg(args, name, fallback.ToString()),
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
