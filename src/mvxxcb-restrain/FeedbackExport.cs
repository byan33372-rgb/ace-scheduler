using System.IO.Compression;

namespace MvxxcbRestrain;

/// <summary>
/// One-click feedback bundle: zips the log, recent session reports and both
/// config files to the desktop so they can be shared as-is. Reads with
/// FileShare-compatible APIs only - the logger may be mid-write.
/// </summary>
public static class FeedbackExport
{
    public static string Build()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mvxxcb-restrain");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"mvxxcb-restrain-feedback-{stamp}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var added = 0;
        void Add(string path, string entry)
        {
            try
            {
                if (File.Exists(path))
                {
                    zip.CreateEntryFromFile(path, entry);
                    added++;
                }
            }
            catch
            {
                // one unreadable file must not break the bundle
            }
        }

        Add(Path.Combine(dir, "log.txt"), "log.txt");
        Add(Path.Combine(dir, "log.txt.bak"), "log.txt.bak");
        Add(Path.Combine(dir, "enabled.txt"), "enabled.txt");
        Add(Path.Combine(dir, "foreground-processes.txt"), "foreground-processes.txt");

        var sessions = Path.Combine(dir, "sessions");
        if (Directory.Exists(sessions))
        {
            // newest first, bounded so a long history cannot balloon the zip
            foreach (var f in Directory.GetFiles(sessions)
                         .OrderByDescending(f => File.GetLastWriteTime(f))
                         .Take(10))
            {
                Add(f, "sessions/" + Path.GetFileName(f));
            }
        }

        if (added == 0) throw new IOException("没有可打包的文件（找不到运行数据目录）");
        return zipPath;
    }
}
