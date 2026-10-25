using ReaLTaiizor.Forms;
using ReaLTaiizor.Controls;
using ReaLTaiizor.Colors;

namespace MvxxcbRestrain;

/// <summary>
/// Builds the control panel on ReaLTaiizor's Material dark theme with a mint
/// accent: real Form subclass (minimize/tray flow intact), CyberButton neon
/// actions, DPI-aware scaling handled at the entry point.
/// </summary>
internal static class PanelBuilder
{
    public sealed class Result
    {
        public required MaterialForm Form { get; init; }
        public required Label StatusLabel { get; init; }
        public required Label HeadLabel { get; init; }
        public required StatusDot HeadDot { get; init; }
        public required CyberButton ToggleButton { get; init; }
        public required CyberButton FeedbackButton { get; init; }
    }

    public static Result Build(bool elevated, Action toggleClicked, Action feedbackClicked)
    {
        var form = new MaterialForm
        {
            Text = "mvxxcb-restrain 控制面板",
            FormBorderStyle = FormBorderStyle.FixedSingle,
            FormStyle = ReaLTaiizor.Enum.Material.FormStyles.ActionBar_48,
            MaximizeBox = false,
            MinimizeBox = true,               // minimize to taskbar
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(412, 268),
            ShowInTaskbar = true,             // reachable while minimized
        };
        var skin = form.SkinManager;
        skin.Theme = ReaLTaiizor.Manager.MaterialSkinManager.Themes.DARK;
        skin.ColorScheme = new MaterialColorScheme(
            Color.FromArgb(0x15, 0x1F, 0x2E),   // primary: deep navy title bar
            Color.FromArgb(0x0E, 0x15, 0x23),   // dark primary
            UiTheme.Accent,                      // light primary: mint accent line
            UiTheme.Accent,                      // accent
            UiTheme.PrimaryText);                // text shade

        var statusCard = new RoundedCard { Location = new Point(18, 56), Size = new Size(376, 78) };
        var headDot = new StatusDot { Location = new Point(18, 20) };
        var headLabel = new Label
        {
            Text = "调度监控运行中",
            AutoSize = false,
            Font = UiTheme.Head,
            ForeColor = UiTheme.PrimaryText,
            Location = new Point(46, 12),
            Size = new Size(314, 26),
        };
        var statusLabel = new Label
        {
            Text = "当前状态：…",
            AutoSize = false,
            Font = UiTheme.Ui,
            ForeColor = UiTheme.DimText,
            Location = new Point(46, 44),
            Size = new Size(312, 24),
        };
        statusCard.Controls.Add(headDot);
        statusCard.Controls.Add(headLabel);
        statusCard.Controls.Add(statusLabel);

        var badgeText = elevated ? "●  运行权限：已提权" : "●  运行权限：普通（未提权，观测可能受限）";
        var badgeW = TextRenderer.MeasureText(badgeText, UiTheme.Small).Width + 24;
        var badge = new System.Windows.Forms.Panel
        {
            Location = new Point(18, 146),
            Size = new Size(badgeW, 26),
            BackColor = Color.Transparent,
        };
        badge.Paint += (_, e) =>
        {
            var r = new Rectangle(0, 0, badge.Width - 1, badge.Height - 1);
            var (border, text) = elevated ? (UiTheme.Accent, UiTheme.Accent) : (UiTheme.Warn, UiTheme.Warn);
            var fill = elevated ? UiTheme.AccentDark : Color.FromArgb(0x33, 0x25, 0x18);
            using var path = UiTheme.Rounded(r, 13);
            using var f = new SolidBrush(fill);
            using var p = new Pen(border);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(f, path);
            e.Graphics.DrawPath(p, path);
            TextRenderer.DrawText(e.Graphics, badgeText,
                UiTheme.Small, r with { X = 1, Y = 1 }, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        var toggle = new CyberButton
        {
            TextButton = "…",
            Size = new Size(212, 42),
            Location = new Point(18, 184),
            Rounding = true,
            RoundingInt = 21,
            Lighting = false,
        };
        toggle.Click += (_, _) => toggleClicked();

        var feedback = new CyberButton
        {
            TextButton = "反馈数据",
            Size = new Size(150, 42),
            Location = new Point(244, 184),
            Rounding = true,
            RoundingInt = 21,
            Lighting = false,
        };
        feedback.Click += (_, _) => feedbackClicked();

        var footer = new Label
        {
            Text = $"v{typeof(PanelBuilder).Assembly.GetName().Version?.ToString(3)} · 仅本地调度 · 明细见日志",
            AutoSize = false,
            Font = UiTheme.Small,
            ForeColor = Color.FromArgb(0x59, 0x67, 0x80),
            BackColor = Color.Transparent,
            Location = new Point(18, 240),
            Size = new Size(376, 20),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        form.Controls.Add(statusCard);
        form.Controls.Add(badge);
        form.Controls.Add(toggle);
        form.Controls.Add(feedback);
        form.Controls.Add(footer);

        return new Result
        {
            Form = form,
            StatusLabel = statusLabel,
            HeadLabel = headLabel,
            HeadDot = headDot,
            ToggleButton = toggle,
            FeedbackButton = feedback,
        };
    }
}
