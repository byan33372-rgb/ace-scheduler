using System.Diagnostics;
using System.Text.Json;

namespace MvxxcbRestrain;

/// <summary>
/// Headless mode for sandbox validation (v0.3 release gate).
/// Multi-target: --target-pids "a,b,c" spins one independent state machine per
/// scanner process with a shared foreground signal - mirroring reality, where
/// ACE runs several scanner processes at once. Same file contract as the trial
/// limiter (control file + JSONL events), plus a per-event "pid" field.
/// </summary>
public static class SelfTest
{
    private sealed record Evt(string ts, string evt, string? state, double? mbps, string? action, int? pid, bool? fgReal = null);

    private sealed class TargetCtx
    {
        public required Process Proc;
        public SchedulerStateMachine Engine = null!;
        public DateTimeOffset LastTs;
        public double LastBytes;
        public int NullStreak; // sampler-based liveness, same rule as Program.cs
    }

    public static int Run(string[] args)
    {
        var pids = new List<int>();
        var multi = StrArg(args, "--target-pids", "");
        if (!string.IsNullOrEmpty(multi))
        {
            foreach (var part in multi.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var v)) pids.Add(v);
            }
        }
        else
        {
            var single = IntArg(args, "--target-pid", 0);
            if (single != 0) pids.Add(single);
        }
        if (pids.Count == 0) return 2;

        var controlFile = StrArg(args, "--control", "limiter.ctl");
        var logFile = StrArg(args, "--log", "limiter.jsonl");
        var foregroundFile = StrArg(args, "--foreground", "");
        var options = new SchedulerOptions
        {
            BurstMBs = DoubleArg(args, "--burst-mbs", 5),
            ProbeSeconds = DoubleArg(args, "--probe-sec", 5),
        };
        var pollMs = IntArg(args, "--poll-ms", 500);
        var fullMask = ProcessThrottle.FullAffinityMask();

        void Log(int pid, Evt e)
        {
            try { File.AppendAllText(logFile, JsonSerializer.Serialize(e) + Environment.NewLine); }
            catch { /* logging must never kill the loop */ }
        }

        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("o");

        var targets = new List<TargetCtx>();
        foreach (var pid in pids)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                var ctx = new TargetCtx { Proc = proc };
                ctx.Engine = new SchedulerStateMachine(
                    options,
                    throttle => ProcessThrottle.Apply(ctx.Proc.Id, ctx.Proc.ProcessName, throttle, fullMask),
                    ev =>
                    {
                        var kind = ev.Kind switch
                        {
                            "throttle" => "burst-detected",
                            "probe" => "probe-start",
                            "rethrottle" => "probe-scanning",
                            "release" => "scan-ended",
                            _ => ev.Kind,
                        };
                        Log(pid, new Evt(Utc(ev.Timestamp), kind, null, ev.RateMBps, ev.Detail, pid));
                    });
                targets.Add(ctx);
                Log(pid, new Evt(Utc(DateTimeOffset.UtcNow), "start", null, null,
                    $"target={pid} burstMBs={options.BurstMBs} probeSec={options.ProbeSeconds}", pid));
            }
            catch
            {
                Log(pid, new Evt(Utc(DateTimeOffset.UtcNow), "target-unopenable", null, null, null, pid));
            }
        }
        if (targets.Count == 0) return 2;

        IoSampler.GetCumulativeReadBytes(targets[0].Proc.Id); // warm up the sampler

        while (true)
        {
            Thread.Sleep(pollMs);
            if (ReadControl(controlFile) == "stop") break;
            var fgBusy = ReadControl(foregroundFile) != "idle"; // missing file / any other value = busy
            var now = DateTimeOffset.UtcNow;

            var gone = new List<int>();
            foreach (var ctx in targets.ToArray())
            {
                // Liveness mirrors Program.cs: 10 consecutive unreadable IO
                // counter ticks = gone. No Refresh()/HasExited - that pattern
                // access-denies on protected processes and flaps.
                var bytes = IoSampler.GetCumulativeReadBytes(ctx.Proc.Id);
                if (bytes is null)
                {
                    if (++ctx.NullStreak >= 10)
                    {
                        Log(ctx.Proc.Id, new Evt(Utc(now), "target-gone", null, null, null, ctx.Proc.Id));
                        gone.Add(ctx.Proc.Id);
                    }
                    continue;
                }
                ctx.NullStreak = 0;
                var result = ctx.Engine.OnSample(bytes.Value, now, fgBusy);
                // Exercise the REAL foreground sensor every tick so the sandbox
                // covers ForegroundMonitor itself, not just the control-file
                // simulation (external review: structural blind spot).
                Log(ctx.Proc.Id, new Evt(Utc(now), "sample", result.State.ToString(), result.RateMBps, null, ctx.Proc.Id,
                    ForegroundMonitor.IsForegroundBusy()));
            }
            foreach (var pid in gone)
            {
                var ctx = targets.First(t => t.Proc.Id == pid);
                try { ctx.Proc.Dispose(); } catch { }
                targets.Remove(ctx);
            }
            if (targets.Count == 0)
            {
                Log(0, new Evt(Utc(DateTimeOffset.UtcNow), "all-targets-gone", null, null, null, 0));
                break;
            }
        }

        Log(0, new Evt(Utc(DateTimeOffset.UtcNow), "stop", null, null, null, 0));
        foreach (var ctx in targets) { try { ctx.Proc.Dispose(); } catch { } }
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
