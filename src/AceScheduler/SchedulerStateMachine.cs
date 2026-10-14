namespace AceScheduler;

public enum SchedulerState
{
    Idle,
    Throttled,
    Probe,
}

public sealed class SchedulerOptions
{
    /// <summary>Read-rate threshold (MB/s) that counts as a scan burst.</summary>
    public double BurstMBs { get; init; } = 5;

    /// <summary>Continuous throttled wall-time before a probe window opens.</summary>
    public double ProbeSeconds { get; init; } = 5;

    /// <summary>Consecutive burst samples required to enter THROTTLED.</summary>
    public int BurstConfirmSamples { get; init; } = 2;
}

public sealed record SchedulerEvent(DateTimeOffset Timestamp, string Kind, double RateMBps, string Detail);

/// <summary>
/// Port of trial/AceScheduler.Trial.ps1 (validated 5/5 PASS, 2026-10-14).
/// IDLE -> (burst confirmed) THROTTLED -> (ProbeSeconds elapsed) PROBE ->
/// rate still high = re-throttle / rate low = scan truly ended -> IDLE.
/// The probe prevents the starvation feedback loop: throttling collapses the
/// scanner's IO rate, which by rate alone would fake a "scan ended" signal.
/// </summary>
public sealed class SchedulerStateMachine
{
    private readonly SchedulerOptions _options;
    private readonly Action<bool> _applyThrottle; // true = throttle, false = restore
    private readonly Action<SchedulerEvent> _log;

    private SchedulerState _state = SchedulerState.Idle;
    private int _burstRun;
    private double _throttledSeconds;
    private DateTimeOffset _lastTs;
    private double _lastBytes;

    public SchedulerState State => _state;

    public SchedulerStateMachine(SchedulerOptions options, Action<bool> applyThrottle, Action<SchedulerEvent> log)
    {
        _options = options;
        _applyThrottle = applyThrottle;
        _log = log;
    }

    /// <summary>Feed one cumulative-read-bytes observation from the sampler.</summary>
    public SchedulerState OnSample(double cumulativeReadBytes, DateTimeOffset now)
    {
        if (_lastTs == default)
        {
            _lastTs = now;
            _lastBytes = cumulativeReadBytes;
            return _state;
        }

        var dt = (now - _lastTs).TotalSeconds;
        var dBytes = cumulativeReadBytes - _lastBytes;
        _lastTs = now;
        _lastBytes = cumulativeReadBytes;
        if (dt < 0.2) return _state; // caller clock not ready yet
        var mbps = dBytes / 1_048_576 / dt;

        switch (_state)
        {
            case SchedulerState.Idle:
                _burstRun = mbps >= _options.BurstMBs ? _burstRun + 1 : 0;
                if (_burstRun >= _options.BurstConfirmSamples)
                {
                    _applyThrottle(true);
                    _state = SchedulerState.Throttled;
                    _throttledSeconds = 0;
                    _log(new SchedulerEvent(now, "throttle", mbps, "burst detected"));
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
                    _applyThrottle(true);
                    _state = SchedulerState.Throttled;
                    _throttledSeconds = 0;
                    _log(new SchedulerEvent(now, "rethrottle", mbps, "probe: still scanning"));
                }
                else
                {
                    _applyThrottle(false);
                    _state = SchedulerState.Idle;
                    _burstRun = 0;
                    _log(new SchedulerEvent(now, "release", mbps, "probe: scan ended"));
                }
                break;
        }

        return _state;
    }
}
