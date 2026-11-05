using System.Drawing.Drawing2D;

namespace RGBcontroller.Agent.Tray;

/// <summary>
/// A small on-screen note after every hotkey, like the Windows volume flyout: what the strip does now. Without it a
/// hotkey that changes nothing visible (brightness up at 100%, a slow Radial mode, a strip in Off) looked like a frozen
/// Agent (stage 4 gate, three reports on 2026-11-04). Click-through, never takes focus, hides after a moment; the
/// window exists only after the first hotkey.
/// </summary>
internal sealed class HotkeyOsd : Form
{
    private static readonly TimeSpan ShowTime = TimeSpan.FromMilliseconds(1600);

    private readonly System.Windows.Forms.Timer _hide = new() { Interval = (int)ShowTime.TotalMilliseconds };
    private string _title = "";
    private string _detail = "";
    private int? _level;

    public HotkeyOsd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = TrayTheme.Background;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = LogicalToDeviceUnits(new Size(300, 72));
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int ToolWindow = 0x80, Transparent = 0x20, NoActivate = 0x08000000, Topmost = 0x8;
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindow | Transparent | NoActivate | Topmost;
            return parameters;
        }
    }

    /// <param name="level">0-100 draws a bar (brightness); null shows no bar.</param>
    public void ShowNote(string title, string detail, int? level)
    {
        _title = title;
        _detail = detail;
        _level = level;
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Bottom - Height - LogicalToDeviceUnits(64));
        Invalidate();
        if (!Visible)
        {
            Show();
        }

        _hide.Stop();
        _hide.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        using (var border = new Pen(TrayTheme.Border))
        {
            g.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
        }

        using (var accent = new SolidBrush(TrayTheme.Accent))
        {
            g.FillRectangle(accent, 0, 0, LogicalToDeviceUnits(3), bounds.Height);
        }

        var pad = LogicalToDeviceUnits(16);
        using var titleFont = new Font("Bahnschrift", 11f, FontStyle.Bold, GraphicsUnit.Point);
        using var detailFont = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
        TextRenderer.DrawText(g, _title, titleFont, new Point(pad, LogicalToDeviceUnits(10)), TrayTheme.Text);
        TextRenderer.DrawText(g, _detail, detailFont, new Rectangle(pad, LogicalToDeviceUnits(10), bounds.Width - 2 * pad, LogicalToDeviceUnits(22)),
            TrayTheme.DimText, TextFormatFlags.Right);

        if (_level is { } level)
        {
            var bar = new Rectangle(pad, LogicalToDeviceUnits(44), bounds.Width - 2 * pad, LogicalToDeviceUnits(6));
            using var track = new SolidBrush(TrayTheme.Border);
            using var fill = new SolidBrush(TrayTheme.Accent);
            g.FillRectangle(track, bar);
            g.FillRectangle(fill, bar.X, bar.Y, bar.Width * Math.Clamp(level, 0, 100) / 100, bar.Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hide.Dispose();
        }

        base.Dispose(disposing);
    }
}
