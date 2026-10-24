using System.Runtime.InteropServices;

namespace MvxxcbRestrain;

/// <summary>
/// Foreground "something to yield to" signal, hybrid by design:
/// 1. Identity: the foreground window belongs to a process listed in the
///    user-editable foreground-processes.txt (one image name per line, '#'
///    comments). This matches the objective exactly - "the configured
///    program never loses" - independent of window mode, DPI and monitor
///    count, and it covers borderless-windowed games the shell does not
///    report as fullscreen. The list ships empty: no names are hardcoded.
/// 2. Geometry fallback: SHQueryUserNotificationState (QUNS_BUSY /
///    QUNS_PRESENTATION_MODE), the shell's own fullscreen verdict.
///    GetWindowRect-vs-screen-bounds was rejected because DWM invisible
///    borders make ANY maximized window pass that test.
/// Detection failure is conservative: busy=false (never gate throttling on
/// a broken sensor - it only decides WHEN to yield, see docs/design.md).
/// </summary>
public static class ForegroundMonitor
{
    private const int QunsBusy = 2;              // full-screen application running
    private const int QunsPresentationMode = 4;  // presentation / do-not-disturb

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static string[] _identityNames = Array.Empty<string>();
    private static string? _listPath;
    private static DateTime _listStampUtc = DateTime.MinValue;

    /// <summary>Point the identity list at its config file (called once at startup).</summary>
    public static void Configure(string identityListPath) => _listPath = identityListPath;

    /// <summary>True when the foreground is a configured process or a fullscreen app.</summary>
    public static bool IsForegroundBusy()
    {
        try
        {
            return IsConfiguredForegroundProcess() || IsFullscreenApp();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The shell's own fullscreen verdict. Public for selftest coverage.</summary>
    public static bool IsFullscreenApp()
    {
        try
        {
            var hr = SHQueryUserNotificationState(out var state);
            if (hr < 0) return false;
            return state == QunsBusy || state == QunsPresentationMode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsConfiguredForegroundProcess()
    {
        RefreshListIfNeeded();
        if (_identityNames.Length == 0) return false;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return false;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (int)pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buf = new char[512];
            var len = buf.Length;
            if (!QueryFullProcessImageName(handle, 0, buf, ref len)) return false;
            var image = new string(buf, 0, len);
            var name = Path.GetFileNameWithoutExtension(image);
            foreach (var n in _identityNames)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Re-read the identity list only when the file changed on disk.</summary>
    private static void RefreshListIfNeeded()
    {
        if (_listPath is null) return;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(_listPath);
            if (stamp == _listStampUtc) return;
            _listStampUtc = stamp;
            var names = new List<string>();
            foreach (var raw in File.ReadAllLines(_listPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) line = line[..^4];
                if (line.Length > 0) names.Add(line);
            }
            _identityNames = names.ToArray();
        }
        catch
        {
            // unreadable list: keep the last successfully loaded names
        }
    }

    /// <summary>Diagnostics for the log: list size, foreground image, both signals.</summary>
    public static string DebugState()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out var pid);
            var image = "?";
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, (int)pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buf = new char[512];
                    var len = buf.Length;
                    if (QueryFullProcessImageName(handle, 0, buf, ref len)) image = new string(buf, 0, len);
                }
                finally { CloseHandle(handle); }
            }
            var first = _identityNames.Length > 0 ? _identityNames[0] : "-";
            return $"list={_identityNames.Length}({first}) fgpid={pid} fgimage={image} busy={IsForegroundBusy()}";
        }
        catch (Exception ex)
        {
            return "debug-failed: " + ex.Message;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, char[] exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
