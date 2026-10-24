using System.IO.Compression;

namespace MvxxcbRestrain;

/// <summary>
/// One-click feedback bundle for tuning sessions ("I just finished gaming,
/// here is what happened"). Picks the most recent session that actually
/// contains game activity (game-start/game-exit events), or the newest one
/// when no game was seen, and packs it as the PRIMARY artifact together with
/// the human summary, the config-at-the-time, the log, and a README that
/// documents version, default parameters and the JSONL event vocabulary.
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

        var sessions = Path.Combine(dir, "sessions");
        var sessionFiles = Directory.Exists(sessions)
            ? Directory.GetFiles(sessions, "session-*.jsonl")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(20)
                .ToList()
            : new List<string>();

        // primary pick: newest session with actual game activity
        string? primary = null;
        var reason = "未找到任何会话记录";
        foreach (var f in sessionFiles)
        {
            var hasGame = false;
            try { hasGame = File.ReadAllText(f).Contains("\"game-start\"") || File.ReadAllText(f).Contains("\"game-exit\""); }
            catch { }
            if (hasGame) { primary = f; reason = $"选中最近含游戏活动的会话: {Path.GetFileName(f)}"; break; }
        }
        if (primary is null && sessionFiles.Count > 0)
        {
            primary = sessionFiles[0];
            reason = $"最近 20 个会话均未检测到游戏进程,选中最新的: {Path.GetFileName(primary)}";
        }

        var version = typeof(FeedbackExport).Assembly.GetName().Version?.ToString() ?? "?";
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

        if (primary is not null) Add(primary, "selected/" + Path.GetFileName(primary));
        Add(Path.Combine(sessions, "summary.txt"), "selected/summary.txt");

        var readme = $"""
            mvxxcb-restrain 监测反馈包
            版本: {version}    打包时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            会话选择: {reason}
            提示: summary.txt 是累计追加的,按会话时间找本次段落; selected/session-*.jsonl 是本次完整时间线。

            默认调度参数: 突发阈值 5 MB/s(连续 2 次采样确认) / 限速持续 5s 后开探测窗 / 采样间隔 500ms / 判活 10 次无样本(约 5s) / 重收养冷却 15s / 观测被拒退避 600s

            文件说明:
              selected/session-*.jsonl  本次会话完整时间线(下述事件流)
              selected/summary.txt      中文人读总结(所有会话累计)
              support/                  运行日志与配置现场(前台进程清单、开关状态)
              history/                  更早的会话记录(对比用)

            JSONL 事件速查(kind 字段):
              session-start | session-summary   会话开始/总结落盘
              scanner-found pid= name=          发现并收养扫描进程
              scanner-gone  pid=                扫描进程消失
              rate          pid= mbps= state=   速率样本(突发时每拍 + 空闲 10s 心跳) state: Idle/BurstIdle/Throttled/Probe
              scheduler-throttle/probe/release/rethrottle(=probe 后再限速)/passthrough(桌面空闲放行)/throttle-failed/restore-failed
              observe-denied pid=               采样被拒(权限不足)
              fg-busy/fg-idle                    前台忙/闲切换(清单命中或全屏)
              game-start/game-exit 进程名        游戏进程出现/退出
            """;
        var readmeEntry = zip.CreateEntry("README.txt");
        using (var w = new StreamWriter(readmeEntry.Open()))
        {
            w.Write(readme);
        }
        added++;

        Add(Path.Combine(dir, "log.txt"), "support/log.txt");
        Add(Path.Combine(dir, "log.txt.1"), "support/log.txt.1"); // FileLogger's rotation target
        Add(Path.Combine(dir, "log.txt.bak"), "support/log.txt.bak"); // legacy manual backups, if any
        Add(Path.Combine(dir, "enabled.txt"), "support/enabled.txt");
        Add(Path.Combine(dir, "foreground-processes.txt"), "support/foreground-processes.txt");
        Add(Path.Combine(dir, "games.txt"), "support/games.txt");

        foreach (var f in sessionFiles.Where(f => f != primary).Take(5))
        {
            Add(f, "history/" + Path.GetFileName(f));
        }

        if (added <= 1) throw new IOException("没有可打包的监测数据（未找到会话记录目录）");
        return zipPath;
    }
}
