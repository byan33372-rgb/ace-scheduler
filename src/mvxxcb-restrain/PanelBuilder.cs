using ReaLTaiizor.Forms;
using ReaLTaiizor.Controls;
using ReaLTaiizor.Colors;

namespace MvxxcbRestrain;

/// <summary>
/// Builds the control panel on ReaLTaiizor's Material dark theme following the
/// UI review layout: 20px window padding, 16px card padding, solid primary and
/// quiet secondary actions. Real Form subclass (minimize/tray flow intact),
/// DPI-aware scaling handled at the entry point.
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
            Sizable = false,                  // MaterialForm draws its own chrome and
                                              // ignores FixedSingle unless told explicitly
            FormStyle = ReaLTaiizor.Enum.Material.FormStyles.ActionBar_48,
            MaximizeBox = false,
            MinimizeBox = true,               // minimize to taskbar
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(432, 292),  // 20px side padding; 292 fits the 44px action row + footer
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

        // 20px window padding; the ActionBar_48 title bar takes 48px, card stays at y=56.
        var statusCard = new RoundedCard { Location = new Point(20, 56), Size = new Size(392, 78) };
        // 16px card padding: dot at cardX+16, text column starts at cardX+16+dotWidth (dot = 18px).
        var headDot = new StatusDot { Location = new Point(16, 20) };
        var headLabel = new Label
        {
            Text = "…",                       // ApplyUi overwrites immediately; no preset status wording
            AutoSize = false,
            Font = UiTheme.Head,
            ForeColor = UiTheme.PrimaryText,
            Location = new Point(34, 12),
            Size = new Size(342, 26),
        };
        var statusLabel = new Label
        {
            Text = "当前状态：…",
            AutoSize = false,
            Font = UiTheme.Ui,
            ForeColor = UiTheme.DimText,
            Location = new Point(34, 44),
            Size = new Size(342, 24),
            AutoEllipsis = true,              // review P0: long status lines ellipsize instead of clipping
        };
        statusCard.Controls.Add(headDot);
        statusCard.Controls.Add(headLabel);
        statusCard.Controls.Add(statusLabel);

        // 徽章语义（评审整改）：已提权 = 授权成功 → Success；未提权保持 Warn 提示观测受限。
        // 自绘胶囊与 AutoWidth 逻辑保持不变。
        var badgeText = elevated ? "●  运行权限：已提权" : "●  运行权限：普通（未提权，观测可能受限）";
        var badgeW = TextRenderer.MeasureText(badgeText, UiTheme.Small).Width + 24;
        var badge = new System.Windows.Forms.Panel
        {
            Location = new Point(20, 146),
            Size = new Size(badgeW, 26),
            BackColor = Color.Transparent,
        };
        badge.Paint += (_, e) =>
        {
            var r = new Rectangle(0, 0, badge.Width - 1, badge.Height - 1);
            var (border, text) = elevated ? (UiTheme.Success, UiTheme.Success) : (UiTheme.Warn, UiTheme.Warn);
            var fill = elevated ? Color.FromArgb(0x26, UiTheme.Success) : Color.FromArgb(0x33, 0x25, 0x18);
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

        // 主操作（评审规格）：实心样式 — Accent 底色 + 同色描边（实心无对比描边），高 44、圆角 8。
        // 文案（"启用调度"/"停用调度"）与状态配色由 Program.cs 的 ApplyUi 动态设置，这里只定样式。
        var toggle = new CyberButton
        {
            TextButton = "…",
            Size = new Size(212, 44),
            Location = new Point(20, 184),
            ColorBackground = UiTheme.Accent,
            ColorBackground_Pen = UiTheme.Accent,
            Rounding = true,
            RoundingInt = 8,
            Lighting = false,
        };
        toggle.Click += (_, _) => toggleClicked();

        // 次级操作（评审规格）：安静样式 — Card 底色 + 1px CardBorder 低对比描边，高 44、圆角 8；
        // 与主按钮间距 14px，右缘对齐 20px 窗口内边距。
        var feedback = new CyberButton
        {
            TextButton = "反馈数据",
            Size = new Size(166, 44),
            Location = new Point(246, 184),
            ColorBackground = UiTheme.Card,
            ColorBackground_Pen = UiTheme.CardBorder,
            Rounding = true,
            RoundingInt = 8,
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
            Location = new Point(20, 260),    // 12px above the bottom edge
            Size = new Size(392, 20),
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
