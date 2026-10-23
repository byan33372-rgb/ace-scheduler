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

    /// <summary>Seconds to wait before retrying a failed throttle attempt.</summary>
    public double RetryAfterFailSeconds { get; init; } = 5;
}

public sealed record SchedulerEvent(DateTimeOffset Timestamp, string Kind, double RateMBps, string Detail);

/// <summary>
/// v4: failure-visible scheduler.
/// The throttle callback reports errors (Func&lt;bool,string?&gt;): a failed
/// application NEVER moves the state machine into Throttled - it logs
/// "throttle-failed", exposes LastError, and backs off RetryAfterFailSeconds
/// before retrying. CPU Idle + IO VeryLow + single-core affinity is applied by
/// ProcessThrottle (which also snapshots and restores the original values).
/// The probe prevents the starvation feedback loop: throttling collapses the
/// scanner's IO rate, which by rate alone would fake a "scan ended" signal.
/// Timing uses wall-clock deltas between samples, never tick counts.
/// </summary>
public sealed class SchedulerStateMachine
{
    private readonly SchedulerOptions _options;
    private readonly Func<bool, string?> _applyThrottle; // true = throttle, false = restore; error text or null
    private readonly Action<SchedulerEvent> _log;

    private SchedulerState _state = SchedulerState.Idle;
    private int _burstRun;
    private int _quietRun;
    private double _throttledSeconds;
    private DateTimeOffset _lastTs;
    private double _lastBytes;
    private double _lastRate;
    private DateTimeOffset _lastFailTs = DateTimeOffset.MinValue;

    public SchedulerState State => _state;

    /// <summary>Last throttle/restore error, null when everything is healthy.</summary>
    public string? LastError { get; private set; }

    public SchedulerStateMachine(SchedulerOptions options, Func<bool, string?> applyThrottle, Action<SchedulerEvent> log)
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
                if (_burstRun >= _options.BurstConfirmSamples && busy && InBackoff(now) is false)
                {
                    if (TryThrottle(now, mbps, "burst detected, foreground busy"))
                        _state = SchedulerState.Throttled;
                }
                break;

            case SchedulerState.BurstIdle:
                if (busy && InBackoff(now) is false)
                {
                    if (TryThrottle(now, mbps, "foreground turned busy mid-scan"))
                        _state = SchedulerState.Throttled;
                }
                else if (!busy && mbps < _options.BurstMBs / 2)
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
                    var restoreErr = _applyThrottle(false);
                    if (restoreErr != null)
                    {
                        LastError = restoreErr;
                        _log(new SchedulerEvent(now, "restore-failed", mbps, restoreErr));
                        // stay Throttled and retry on the next probe cycle
                    }
                    else
                    {
                        LastError = null;
                        _state = SchedulerState.Probe;
                        _log(new SchedulerEvent(now, "probe", mbps, $"throttled {_throttledSeconds:F1}s, releasing probe window"));
                    }
                }
                break;

            case SchedulerState.Probe:
                if (mbps >= _options.BurstMBs)
                {
                    if (busy && InBackoff(now) is false)
                    {
                        if (TryThrottle(now, mbps, "probe: still scanning, foreground busy"))
                            _state = SchedulerState.Throttled;
                    }
                    else if (!busy)
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

    private bool InBackoff(DateTimeOffset now) => (now - _lastFailTs).TotalSeconds < _options.RetryAfterFailSeconds;

    private bool TryThrottle(DateTimeOffset now, double mbps, string detail)
    {
        var err = _applyThrottle(true);
        if (err != null)
        {
            LastError = err;
            _lastFailTs = now;
            _log(new SchedulerEvent(now, "throttle-failed", mbps, detail + " -> " + err));
            return false;
        }
        LastError = null;
        _throttledSeconds = 0;
        _burstRun = 0;
        _quietRun = 0;
        _log(new SchedulerEvent(now, "throttle", mbps, detail));
        return true;
    }
}
