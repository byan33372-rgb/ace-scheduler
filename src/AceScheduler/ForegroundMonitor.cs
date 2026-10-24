using System.Runtime.InteropServices;

namespace AceScheduler;

/// <summary>
/// Production foreground signal: a fullscreen foreground window counts as
/// "something to yield to". Deliberately conservative - it only gates WHEN
/// throttling is applied, never touches the throttled process itself.
/// Uses SHQueryUserNotificationState (the shell's own fullscreen verdict)
/// because GetWindowRect vs screen bounds reports ANY maximized window as
/// fullscreen: DWM invisible borders make the rect ~8px larger per side,
/// which would keep foregroundBusy permanently true and make BurstIdle
/// (idle passthrough) unreachable.
/// </summary>
public static class ForegroundMonitor
{
    private const int QunsBusy = 2;              // full-screen application running
    private const int QunsPresentationMode = 4;  // user gave a presentation / do-not-disturb

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>True when the user is running a full-screen application.</summary>
    public static bool IsFullscreenApp()
    {
        try
        {
            var hr = SHQueryUserNotificationState(out var state);
            if (hr < 0) return false; // detection failure must never gate throttling on
            return state == QunsBusy || state == QunsPresentationMode;
        }
        catch
        {
            return false;
        }
    }
}
