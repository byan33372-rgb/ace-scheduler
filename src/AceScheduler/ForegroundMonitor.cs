using System.Runtime.InteropServices;

namespace AceScheduler;

/// <summary>
/// Production foreground signal: a fullscreen foreground window counts as
/// "something to yield to". Deliberately conservative - it only gates WHEN
/// throttling is applied, never touches the throttled process itself.
/// v0.3 roadmap: extend with per-process foreground heuristics.
/// </summary>
public static class ForegroundMonitor
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>True when the foreground window covers an entire screen.</summary>
    public static bool IsFullscreenApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var r)) return false;
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is null) return false;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            return w >= screen.Bounds.Width && h >= screen.Bounds.Height;
        }
        catch
        {
            return false; // detection failure must never crash the loop
        }
    }
}
