using System.Text;

namespace AceScheduler;

/// <summary>
/// Release-safe file logger (Debug.WriteLine is compiled out of Release
/// builds). Writes to %LOCALAPPDATA%\mvxxcb-restrain\log.txt with 512KB
/// rotation; never throws into the caller's loop.
/// </summary>
public static class FileLogger
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string LogPath
    {
        get
        {
            if (_path is null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "mvxxcb-restrain");
                try { Directory.CreateDirectory(dir); } catch { }
                _path = Path.Combine(dir, "log.txt");
            }
            return _path;
        }
    }

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    var old = LogPath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    fi.MoveTo(old);
                }
                File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("o") + " " + line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }
}
