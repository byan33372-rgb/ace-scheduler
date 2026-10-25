using System.Text.Json;

namespace MvxxcbRestrain;

/// <summary>
/// Builds the "调度控制台" panel: orthogonal grid, deep navy surfaces, 1px
/// hairlines, rectangular buttons. Native title bar (DWM dark) supplies the
/// window controls — no second chrome. Business state flows in through
/// Program.cs's ApplyUi; the log table reads real session JSONL events only.
/// </summary>
internal static class PanelBuilder
{
    public sealed class Result
    {
        public required Form Form { get; init; }
        public required Label StatusLabel { get; init; }
        public required Label HeadLabel { get; init; }
        public required StatusDot HeadDot { get; init; }
        public required Label PermValue { get; init; }
        public required FlatButton ToggleButton { get; init; }
        public required FlatButton FeedbackButton { get; init; }
        public required DataGridView LogGrid { get; init; }
    }

    private static readonly (string Evt, string Label, string Kind)[] EventMap =
    {
        ("session-start",      "会话开始",     "info"),
        ("scanner-found",      "发现扫描进程", "info"),
        ("scanner-gone",       "扫描进程退出", "info"),
        ("scheduler-throttle", "限速施加",     "good"),
        ("scheduler-probe",    "探测窗口",     "info"),
        ("scheduler-release",  "解除并恢复",   "good"),
        ("observe-denied",     "观测被拒",     "bad"),
        ("game-start",         "游戏启动",     "info"),
        ("game-exit",          "游戏退出",     "info"),
        ("session-summary",    "会话报告",     "info"),
    };

    public static Result Build(bool elevated, Action toggleClicked, Action feedbackClicked, Action openLogs)
    {
        var form = new Form
        {
            Text = "调度控制台 · mvxxcb-restrain",
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = true,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true,
            BackColor = UiTheme.Window,
            Font = UiTheme.Ui,
        };
        form.Shown += (_, _) => UiTheme.TryDarkTitleBar(form.Handle);

        // layout constants are authored at 96 DPI; scale deterministically.
        // AutoScaleMode stays None so WinForms never double-scales.
        float k = form.DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * k); // size
        Point P(int x, int y) => new(S(x), S(y)); // position
        Size Z(int w, int h) => new(S(w), S(h)); // extent
        form.ClientSize = Z(432, 444);

        // ---- status & permission panel: two columns split by a 1px vertical line ----
        var statusPanel = new FlatPanel { Location = P(20, 16), Size = Z(392, 88) };

        var headDot = new StatusDot { Location = P(16, 24), Size = Z(14, 14) };
        var headLabel = new Label
        {
            Text = "…",
            AutoSize = true,
            Font = UiTheme.Head,
            ForeColor = UiTheme.PrimaryText,
            Location = P(42, 14),
        };
        var statusLabel = new Label
        {
            Text = "…",
            AutoSize = false,
            AutoEllipsis = true,
            Font = UiTheme.Ui,
            ForeColor = UiTheme.DimText,
            Location = P(42, 48),
            Size = Z(172, 24),
        };
        var permTitle = new Label
        {
            Text = "运行权限",
            AutoSize = false,
            Font = UiTheme.Small,
            ForeColor = UiTheme.DimText,
            Location = P(252, 16),
            Size = Z(122, 20),
        };
        var permValue = new Label
        {
            Text = elevated ? "已提权" : "未提权（观测受限）",
            AutoSize = false,
            AutoEllipsis = true,
            Font = UiTheme.UiBold,
            ForeColor = elevated ? UiTheme.Success : UiTheme.Danger,
            Location = P(252, 44),
            Size = Z(122, 26),
        };

        statusPanel.Controls.Add(headDot);
        statusPanel.Controls.Add(headLabel);
        statusPanel.Controls.Add(statusLabel);
        statusPanel.Controls.Add(permTitle);
        statusPanel.Controls.Add(permValue);
        statusPanel.Paint += (_, e) =>
        {
            // single vertical hairline splitting status | permission columns
            e.Graphics.DrawLine(new Pen(UiTheme.Line), S(234), S(12), S(234), statusPanel.Height - S(12));
        };

        // ---- actions: equal-height rectangles, primary solid / secondary outlined ----
        var toggle = new FlatButton
        {
            Text = "…",
            ButtonKind = FlatButton.Kind.Primary,
            Location = P(20, 120),
            Size = Z(190, 44),
        };
        toggle.Click += (_, _) => toggleClicked();

        var feedback = new FlatButton
        {
            Text = "反馈数据",
            ButtonKind = FlatButton.Kind.Secondary,
            Location = P(222, 120),
            Size = Z(190, 44),
        };
        feedback.Click += (_, _) => feedbackClicked();

        // ---- run log: real session events, horizontal hairlines, internal scroll ----
        var logPanel = new FlatPanel { Location = P(20, 180), Size = Z(392, 200) };

        var logTitle = new Label
        {
            Text = "运行日志",
            AutoSize = true,
            Font = UiTheme.UiBold,
            ForeColor = UiTheme.PrimaryText,
            Location = P(16, 12),
        };
        var logOpen = new LinkLabel
        {
            Text = "查看全部",
            AutoSize = true,
            Font = UiTheme.Small,
            LinkColor = UiTheme.DimText,
            ActiveLinkColor = UiTheme.Accent,
            Location = P(318, 14),
        };
        logOpen.Click += (_, _) => openLogs();

        var grid = new DataGridView
        {
            Location = P(1, 40),
            Size = Z(390, 158),
            BorderStyle = BorderStyle.None,
            BackgroundColor = UiTheme.Card,
            GridColor = UiTheme.Line,
            EnableHeadersVisualStyles = false,
            RowHeadersVisible = false,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, // horizontal lines only
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ScrollBars = ScrollBars.None, // newest rows only; 查看全部 covers the rest
            Font = UiTheme.Small,
            RowTemplate = { Height = S(30) },
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Card;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.DimText;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.Card;
        grid.ColumnHeadersHeight = S(32);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(8), 0, 0, 0);
        grid.DefaultCellStyle.BackColor = UiTheme.Card;
        grid.DefaultCellStyle.ForeColor = UiTheme.PrimaryText;
        grid.DefaultCellStyle.SelectionBackColor = UiTheme.Surface;
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.PrimaryText;
        grid.DefaultCellStyle.Padding = new Padding(S(8), 0, 0, 0);

        var cTime = new DataGridViewTextBoxColumn { Name = "时间", Width = S(92), SortMode = DataGridViewColumnSortMode.NotSortable };
        var cEvent = new DataGridViewTextBoxColumn { Name = "事件", Width = S(118), SortMode = DataGridViewColumnSortMode.NotSortable };
        var cState = new DataGridViewTextBoxColumn { Name = "状态", Width = S(84), SortMode = DataGridViewColumnSortMode.NotSortable };
        var cDetail = new DataGridViewTextBoxColumn { Name = "详情", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable };
        grid.Columns.AddRange(cTime, cEvent, cState, cDetail);

        var headerLine = new Label
        {
            BackColor = UiTheme.Line,
            Location = new Point(1, 40 + grid.ColumnHeadersHeight),
            Size = new Size(S(390), 1),
        };
        logPanel.Controls.Add(headerLine);
        logPanel.Controls.Add(logTitle);
        logPanel.Controls.Add(logOpen);
        logPanel.Controls.Add(grid);

        // ---- footer: real version + log entry, split by a hairline ----
        var footerLine = new Label
        {
            BackColor = UiTheme.Line,
            Location = P(20, 396),
            Size = new Size(S(392), 1), // hairline: 1 physical px, not scaled
        };
        var version = new Label
        {
            Text = $"版本 v{typeof(PanelBuilder).Assembly.GetName().Version?.ToString(3)}",
            AutoSize = true,
            Font = UiTheme.Small,
            ForeColor = UiTheme.DimText,
            Location = P(20, 410),
        };
        var footerRight = new LinkLabel
        {
            Text = "本地调度 · 明细日志",
            AutoSize = true,
            Font = UiTheme.Small,
            LinkColor = UiTheme.DimText,
            ActiveLinkColor = UiTheme.Accent,
            Location = P(290, 410),
        };
        footerRight.Click += (_, _) => openLogs();

        form.Controls.Add(statusPanel);
        form.Controls.Add(toggle);
        form.Controls.Add(feedback);
        form.Controls.Add(logPanel);
        form.Controls.Add(footerLine);
        form.Controls.Add(version);
        form.Controls.Add(footerRight);

        RefreshLog(grid);

        return new Result
        {
            Form = form,
            StatusLabel = statusLabel,
            HeadLabel = headLabel,
            HeadDot = headDot,
            PermValue = permValue,
            ToggleButton = toggle,
            FeedbackButton = feedback,
            LogGrid = grid,
        };
    }

    /// <summary>
    /// Loads the newest real session JSONL into the grid. No invented rows:
    /// with no session data the table shows a single empty-state row.
    /// </summary>
    public static void RefreshLog(DataGridView grid)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "mvxxcb-restrain", "sessions");
            if (!Directory.Exists(dir)) return;
            var newest = Directory.GetFiles(dir, "session-*.jsonl")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Name)
                .FirstOrDefault();
            if (newest is null) return;

            var rows = new List<(string Time, string Evt, string State, string StateKind, string Detail)>();
            foreach (var line in File.ReadLines(newest.FullName))
            {
                if (rows.Count >= 200) break;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }
                using (doc)
                {
                    var evt = doc.RootElement.GetProperty("evt").GetString();
                    if (evt is "rate" or "fg-busy" or "fg-idle") continue; // per-sample noise
                    var ts = doc.RootElement.GetProperty("ts").GetString();
                    var detail = doc.RootElement.TryGetProperty("detail", out var d) ? (d.GetString() ?? "") : "";
                    var map = EventMap.FirstOrDefault(m => m.Evt == evt);
                    var label = map.Evt is null ? (evt ?? "") : map.Label;
                    var kind = map.Evt is null ? "info" : map.Kind;
                    rows.Add((ts?.Length >= 19 ? ts[11..19] : ts ?? "", label,
                        kind switch { "good" => "成功", "bad" => "警示", _ => "提示" },
                        kind, detail ?? ""));
                }
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            var visible = Math.Max(1, (grid.Height - grid.ColumnHeadersHeight) / grid.RowTemplate.Height);
            if (rows.Count == 0)
            {
                grid.Rows.Add("—", "暂无运行记录", "—", "调度事件将在此显示");
                grid.Rows[0].DefaultCellStyle.ForeColor = UiTheme.DimText;
            }
            else
            {
                foreach (var r in rows.AsEnumerable().Reverse().Take(visible))
                {
                    var i = grid.Rows.Add(r.Time, r.Evt, r.State, r.Detail);
                    var row = grid.Rows[i];
                    row.Cells[2].Style.ForeColor = r.StateKind switch
                    {
                        "good" => UiTheme.Success,
                        "bad" => UiTheme.Danger,
                        _ => UiTheme.DimText,
                    };
                    foreach (DataGridViewCell c in row.Cells)
                        c.ToolTipText = $"{r.Evt} · {r.Detail}";
                }
            }
            grid.ResumeLayout();
        }
        catch
        {
            // log is auxiliary; never break the panel over a read hiccup
        }
    }
}
