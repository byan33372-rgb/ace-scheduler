using System.Runtime.InteropServices;

namespace MvxxcbRestrain;

/// <summary>
/// Design tokens for the orthogonal "调度控制台" panel: deep navy surfaces,
/// 1px hairlines, rectangular components, restrained accents. Geometry rules:
/// 0px radius everywhere, horizontal/vertical lines only, 4px spacing grid.
/// </summary>
internal static class UiTheme
{
    // palette (design spec tokens)
    public static readonly Color Window = Color.FromArgb(0x0B, 0x12, 0x20);      // --app-bg
    public static readonly Color Card = Color.FromArgb(0x10, 0x1C, 0x2F);        // --panel-bg
    public static readonly Color Surface = Color.FromArgb(0x14, 0x22, 0x38);     // --surface-bg
    public static readonly Color Line = Color.FromArgb(0x29, 0x41, 0x5B);        // --line-color
    public static readonly Color PrimaryText = Color.FromArgb(0xE8, 0xF0, 0xFA); // --text-primary
    public static readonly Color DimText = Color.FromArgb(0x91, 0xA5, 0xBF);     // --text-secondary
    public static readonly Color Accent = Color.FromArgb(0x24, 0x77, 0xEE);      // --primary (main op only)
    public static readonly Color AccentHover = Color.FromArgb(0x3D, 0x86, 0xF0);
    public static readonly Color Success = Color.FromArgb(0x18, 0xCF, 0xA0);     // --success
    public static readonly Color Danger = Color.FromArgb(0xF4, 0x5A, 0x70);      // --danger

    public static readonly Font Head = new("Microsoft YaHei UI", 13F, FontStyle.Bold);
    public static readonly Font Ui = new("Microsoft YaHei UI", 10.5F);
    public static readonly Font UiBold = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    public static readonly Font Small = new("Microsoft YaHei UI", 9F);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark native title bar (attribute 20 on 19041+, 19 on 1809).</summary>
    public static void TryDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        _ = DwmSetWindowAttribute(hwnd, 19, ref on, 4);
    }
}

/// <summary>Flat rectangular panel: panel-bg fill + 1px line border, square corners.</summary>
internal sealed class FlatPanel : Panel
{
    public FlatPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var fill = new SolidBrush(UiTheme.Card);
        using var border = new Pen(UiTheme.Line);
        e.Graphics.FillRectangle(fill, r);
        e.Graphics.DrawRectangle(border, r);
    }
}

/// <summary>Small round status marker — the one allowed circular element
/// (state marker), always paired with a text label.</summary>
internal sealed class StatusDot : Control
{
    private System.Windows.Forms.Timer? _pulse;
    private double _phase;

    public StatusDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(14, 14);
        Color = UiTheme.DimText;
    }

    public Color Color { get; set; }

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
        float halo = _pulse is null ? 0.22f : 0.12f + 0.09f * (float)Math.Sin(_phase);
        using (var h = new SolidBrush(Color.FromArgb((int)(halo * 255), Color)))
            g.FillEllipse(h, 0, 0, 14, 14);
        using var b = new SolidBrush(Color);
        g.FillEllipse(b, 3, 3, 8, 8);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pulse?.Stop();
        base.OnHandleDestroyed(e);
    }
}

/// <summary>Rectangular flat button, 0px radius. Primary = solid accent;
/// Secondary = surface bg + 1px accent border. Hover/pressed states included.</summary>
internal sealed class FlatButton : Button
{
    public enum Kind { Primary, Secondary }

    private Kind _kind = Kind.Primary;
    private bool _hover;
    private bool _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = UiTheme.UiBold;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; _down = false; Invalidate(); };
        MouseDown += (_, _) => { _down = true; Invalidate(); };
        MouseUp += (_, _) => { _down = false; Invalidate(); };
    }

    public Kind ButtonKind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        Color fill, edge, text;

        if (_kind == Kind.Primary)
        {
            fill = _hover || _down ? UiTheme.AccentHover : UiTheme.Accent;
            edge = fill;
            text = UiTheme.PrimaryText;
        }
        else
        {
            fill = _hover ? UiTheme.Surface : UiTheme.Card;
            edge = UiTheme.Accent;
            text = UiTheme.PrimaryText;
        }

        using var f = new SolidBrush(fill);
        g.FillRectangle(f, r);
        using var p = new Pen(edge);
        g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

        TextRenderer.DrawText(g, Text, Font, r, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
