using System.Runtime.InteropServices;

namespace MvxxcbRestrain;

/// <summary>Dark "tech accelerator" theme for the control panel: colors, rounded
/// drawing helpers, DWM dark title bar, and the themed controls used by Program.cs.</summary>
internal static class UiTheme
{
    // palette: unified dark tokens from the UI review report (slate base + cyan primary action)
    public static readonly Color Window = Color.FromArgb(0x11, 0x18, 0x27);      // main background
    public static readonly Color Card = Color.FromArgb(0x1F, 0x29, 0x37);        // content surface
    public static readonly Color CardBorder = Color.FromArgb(0x37, 0x41, 0x51);  // dividers / weak stroke
    public static readonly Color PrimaryText = Color.FromArgb(0xF3, 0xF4, 0xF6);
    public static readonly Color DimText = Color.FromArgb(0x9C, 0xA3, 0xAF);
    public static readonly Color Accent = Color.FromArgb(0x08, 0x91, 0xB2);      // primary interactions only
    public static readonly Color AccentDark = Color.FromArgb(0x0B, 0x5E, 0x73);  // dark companion fill for accent (button base/hover)
    public static readonly Color Success = Color.FromArgb(0x34, 0xD3, 0x99);     // 成功/授权——只表达真实成功状态
    public static readonly Color Warn = Color.FromArgb(0xFF, 0x9F, 0x43);        // not elevated (out of report scope)
    public static readonly Color Danger = Color.FromArgb(0xFB, 0x71, 0x85);      // disabled state
    public static readonly Color DangerDark = Color.FromArgb(0x4A, 0x1F, 0x2B);
    public static readonly Color Button2 = Color.FromArgb(0x1F, 0x29, 0x37);     // secondary button surface
    public static readonly Color Button2Hover = Color.FromArgb(0x26, 0x32, 0x43);

    // type ladder from the review report: Head ~18.7px, Ui/UiBold 14px, Small 12px
    public static readonly Font Ui = new("Microsoft YaHei UI", 10.5F);
    public static readonly Font UiBold = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    public static readonly Font Head = new("Microsoft YaHei UI", 14F, FontStyle.Bold);
    public static readonly Font Small = new("Microsoft YaHei UI", 9F);

    public static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark immersive title bar (attribute 19 on 1809, 20 on 19041+).</summary>
    public static void TryDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        _ = DwmSetWindowAttribute(hwnd, 19, ref on, 4);
    }
}

/// <summary>A rounded card panel that paints its own fill and 1px border.</summary>
internal sealed class RoundedCard : Panel
{
    public RoundedCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Card; // match the painted fill so child labels blend in
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiTheme.Rounded(r, 12);
        using var fill = new SolidBrush(UiTheme.Card);
        using var border = new Pen(UiTheme.CardBorder);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Status dot with a soft halo; breathes while the scheduler runs.</summary>
internal sealed class StatusDot : Control
{
    private System.Windows.Forms.Timer? _pulse;
    private double _phase;

    public StatusDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(18, 18);
        Color = UiTheme.DimText;
    }

    public Color Color { get; set; }

    /// <summary>Start/stop the breathing animation (animate only while running).</summary>
    public void SetRunning(bool running)
    {
        if (running && _pulse is null)
        {
            _pulse = new System.Windows.Forms.Timer { Interval = 66 };
            _pulse.Tick += (_, _) => { _phase += 0.13; Invalidate(); };
            _pulse.Start();
        }
        else if (!running && _pulse is not null)
        {
            _pulse.Stop();
            _pulse.Dispose();
            _pulse = null;
            _phase = 0;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var core = new Rectangle(4, 4, 10, 10);
        float halo = _pulse is null ? 0.25f : 0.12f + 0.10f * (float)Math.Sin(_phase);
        using (var h = new SolidBrush(Color.FromArgb((int)(halo * 255), Color)))
            g.FillEllipse(h, 0, 0, 18, 18);
        using var b = new SolidBrush(Color);
        g.FillEllipse(b, core);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pulse?.Stop();
        base.OnHandleDestroyed(e);
    }
}

/// <summary>Flat rounded button with hover states. Accent decides the palette;
/// <see cref="ThemedButton.RefreshState"/> re-derives colors for the enabled/disabled scheduler look.</summary>
internal sealed class ThemedButton : Button
{
    public enum Kind { Primary, Secondary }

    private Kind _kind = Kind.Secondary;
    private bool _hover;
    private bool _schedulerEnabled = true;

    public ThemedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = UiTheme.UiBold;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    public Kind ButtonKind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    /// <summary>For Primary: color follows the scheduler on/off state.</summary>
    public void RefreshState(bool schedulerEnabled)
    {
        _schedulerEnabled = schedulerEnabled;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill, edge, text;

        if (_kind == Kind.Secondary)
        {
            fill = _hover ? UiTheme.Button2Hover : UiTheme.Button2;
            edge = UiTheme.CardBorder;
            text = UiTheme.PrimaryText;
        }
        else if (_schedulerEnabled)
        {
            // live: mint-tinted, hover brightens toward the accent
            fill = _hover ? Color.FromArgb(0x12, 0x3A, 0x33) : UiTheme.AccentDark;
            edge = UiTheme.Accent;
            text = UiTheme.Accent;
        }
        else
        {
            fill = _hover ? Color.FromArgb(0x40, 0x1B, 0x28) : UiTheme.DangerDark;
            edge = UiTheme.Danger;
            text = UiTheme.Danger;
        }

        using var path = UiTheme.Rounded(r, 10);
        using var f = new SolidBrush(fill);
        using var p = new Pen(edge);
        g.FillPath(f, path);
        g.DrawPath(p, path);

        TextRenderer.DrawText(g, Text, Font, r with { X = 1, Y = 1 }, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
