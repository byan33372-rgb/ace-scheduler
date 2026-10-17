namespace AceScheduler;

public enum SchedulerState
{
    /// <summary>No scan activity.</summary>
    Idle,
    /// <summary>Scan burst detected but the desktop is idle - deliberately NOT
    /// throttling, so the scan finishes fast and goes away (docs/design.md:
    /// the objective is "the foreground never loses", not "ACE as slow as possible").</summary>
    BurstIdle,
    Throttled,
    Probe,
}

public sealed record SampleResult(SchedulerState State, double RateMBps);

public sealed class SchedulerOptions
{
    /// <summary>Read-rate threshold (MB/s) that counts as a scan burst.</summary>
    public double BurstMBs { get; init; } = 5;

    /// <summary>Continuous throttled wall-time before a probe window opens.</summary>
    public double ProbeSeconds { get; init; } = 5;

    /// <summary>Consecutive burst samples required to leave Idle.</summary>
    public int BurstConfirmSamples { get; init; } = 2;

    /// <summary>Quiet samples (rate below half the burst threshold) required to
    /// declare a scan ended from BurstIdle.</summary>
    public int QuietConfirmSamples { get; init; } = 2;

    /// <summary>Force the foreground-busy signal on (overrides detection).</summary>
    public bool AlwaysThrottle { get; init; }
}

public sealed record SchedulerEvent(DateTimeOffset Timestamp, string Kind, double RateMBps, string Detail);

/// <summary>
/// v3: foreground-gated scheduler.
/// IDLE -> burst confirmed -> foreground busy ? THROTTLED : BURST_IDLE.
/// BURST_IDLE: foreground turns busy -> THROTTLED; rate quiet -> IDLE (scan
/// finished without us ever throttling - the ideal outcome).
/// THROTTLED -> after ProbeSeconds -> PROBE. PROBE has three outcomes:
/// still scanning + foreground busy -> THROTTLED; still scanning + desktop
/// idle -> BURST_IDLE (release so it finishes); ended -> IDLE.
/// The probe prevents the starvation feedback loop: throttling collapses the
/// scanner's IO rate, which by rate alone would fake a "scan ended" signal.
/// Timing uses wall-clock deltas between samples, never tick counts.
/// </summary>
public sealed class SchedulerStateMachine
{
    private readonly SchedulerOptions _options;
    private readonly Action<bool> _applyThrottle; // true = throttle, false = restore
    private readonly Action<SchedulerEvent> _log;

    private SchedulerState _state = SchedulerState.Idle;
    private int _burstRun;
    private int _quietRun;
    private double _throttledSeconds;
    private DateTimeOffset _lastTs;
    private double _lastBytes;
    private double _lastRate;

    public SchedulerState State => _state;

    public SchedulerStateMachine(SchedulerOptions options, Action<bool> applyThrottle, Action<SchedulerEvent> log)
    {
        _options = options;
        _applyThrottle = applyThrottle;
        _log = log;
    }

    /// <summary>Feed one cumulative-read-bytes observation plus the foreground-busy signal.</summary>
    public SampleResult OnSample(double cumulativeReadBytes, DateTimeOffset now, bool foregroundBusy)
    {
        if (_options.AlwaysThrottle) foregroundBusy = true;

        if (_lastTs == default)
        {
            _lastTs = now;
            _lastBytes = cumulativeReadBytes;
            return new SampleResult(_state, 0);
        }

        var dt = (now - _lastTs).TotalSeconds;
        var dBytes = cumulativeReadBytes - _lastBytes;
        _lastTs = now;
        _lastBytes = cumulativeReadBytes;
        if (dt < 0.2) return new SampleResult(_state, _lastRate); // clock not ready yet
        var mbps = dBytes / 1_048_576 / dt;
        _lastRate = mbps;
        var busy = foregroundBusy;

        switch (_state)
        {
            case SchedulerState.Idle:
                _burstRun = mbps >= _options.BurstMBs ? _burstRun + 1 : 0;
                if (_burstRun >= _options.BurstConfirmSamples)
                {
                    if (busy)
                    {
                        Throttle(now, mbps, "burst detected, foreground busy");
                        _state = SchedulerState.Throttled;
                    }
                    else
                    {
                        _state = SchedulerState.BurstIdle;
                        _burstRun = 0;
                        _log(new SchedulerEvent(now, "passthrough", mbps, "burst detected, desktop idle - letting it finish"));
                    }
                }
                break;

            case SchedulerState.BurstIdle:
                if (busy)
                {
                    Throttle(now, mbps, "foreground turned busy mid-scan");
                    _state = SchedulerState.Throttled;
                }
                else if (mbps < _options.BurstMBs / 2)
                {
                    _quietRun++;
                    if (_quietRun >= _options.QuietConfirmSamples)
                    {
                        _state = SchedulerState.Idle;
                        _burstRun = 0;
                        _quietRun = 0;
                        _log(new SchedulerEvent(now, "release", mbps, "scan ended (never throttled)"));
                    }
                }
                else
                {
                    _quietRun = 0;
                }
                break;

            case SchedulerState.Throttled:
                _throttledSeconds += dt;
                if (_throttledSeconds >= _options.ProbeSeconds)
                {
                    _applyThrottle(false);
                    _state = SchedulerState.Probe;
                    _log(new SchedulerEvent(now, "probe", mbps, $"throttled {_throttledSeconds:F1}s, releasing probe window"));
                }
                break;

            case SchedulerState.Probe:
                if (mbps >= _options.BurstMBs)
                {
                    if (busy)
                    {
                        Throttle(now, mbps, "probe: still scanning, foreground busy");
                        _state = SchedulerState.Throttled;
                    }
                    else
                    {
                        _state = SchedulerState.BurstIdle;
                        _burstRun = 0;
                        _log(new SchedulerEvent(now, "passthrough", mbps, "probe: still scanning, desktop idle - released"));
                    }
                }
                else
                {
                    _state = SchedulerState.Idle;
                    _burstRun = 0;
                    _log(new SchedulerEvent(now, "release", mbps, "probe: scan ended"));
                }
                break;
        }

        return new SampleResult(_state, mbps);
    }

    private void Throttle(DateTimeOffset now, double mbps, string detail)
    {
        _applyThrottle(true);
        _throttledSeconds = 0;
        _burstRun = 0;
        _quietRun = 0;
        _log(new SchedulerEvent(now, "throttle", mbps, detail));
    }
}
