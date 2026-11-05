using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RGBcontroller.Agent.Tray;

/// <summary>Dark cyberpunk palette of the tray menu (SPEC 7.1 and 8): near-black panels, cyan accent, hard edges.</summary>
internal static partial class TrayTheme
{
    public static readonly Color Background = Color.FromArgb(0x0E, 0x11, 0x16);
    public static readonly Color Panel = Color.FromArgb(0x16, 0x1B, 0x22);
    public static readonly Color Hover = Color.FromArgb(0x1C, 0x2A, 0x33);
    public static readonly Color Border = Color.FromArgb(0x2A, 0x33, 0x3D);
    public static readonly Color Accent = Color.FromArgb(0x00, 0xE5, 0xFF);
    public static readonly Color Magenta = Color.FromArgb(0xFF, 0x2B, 0xD6);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xED, 0xF3);
    public static readonly Color DimText = Color.FromArgb(0x7D, 0x85, 0x90);
    public static readonly Color Warning = Color.FromArgb(0xFF, 0xB0, 0x20);

    public static ToolStripRenderer CreateRenderer() => new Renderer();

    /// <summary>Tray icons drawn at the current small-icon size: rainbow ring (ok), amber broken ring (degraded), grey ring (off).</summary>
    /// <summary>The mark's outline: polygon(30% 0, 100% 0, 100% 70%, 70% 100%, 0 100%, 0 30%) in <paramref name="r"/>.</summary>
    private static PointF[] Mark(RectangleF r) =>
    [
        new(r.Left + r.Width * 0.3f, r.Top), new(r.Right, r.Top), new(r.Right, r.Top + r.Height * 0.7f),
        new(r.Left + r.Width * 0.7f, r.Bottom), new(r.Left, r.Bottom), new(r.Left, r.Top + r.Height * 0.3f),
    ];

    /// <summary>Fills the mark with hue by angle around its center (or grey).</summary>
    private static void FillMark(Graphics g, RectangleF r, bool grey)
    {
        using var path = new GraphicsPath();
        path.AddPolygon(Mark(r));
        var clip = g.Clip;
        g.SetClip(path);
        const int Segments = 36;
        var center = new PointF(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var reach = r.Width;
        for (var i = 0; i < Segments; i++)
        {
            using var brush = new SolidBrush(grey ? DimText : FromHue(i * 360.0 / Segments));
            g.FillPie(brush, center.X - reach, center.Y - reach, reach * 2, reach * 2, -90 + i * 360f / Segments, 360f / Segments + 1);
        }

        g.Clip = clip;
    }

    /// <summary>
    /// The application mark (assets/rgbcontroller.ico, drawn by tools/make-icon.py): a rainbow square with two clipped
    /// corners around a dark core with a small mark inside. Degraded adds an amber dot; Off is grey.
    /// </summary>
    public static Icon CreateIcon(TrayIconState state)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var grey = state == TrayIconState.Off;
            FillMark(g, new RectangleF(0, 0, size, size), grey);
            using (var core = new SolidBrush(Background))
            {
                g.FillPolygon(core, Mark(new RectangleF(size * 0.19f, size * 0.19f, size * 0.62f, size * 0.62f)));
            }

            FillMark(g, new RectangleF(size * 0.35f, size * 0.35f, size * 0.30f, size * 0.30f), grey);
            if (state == TrayIconState.Degraded)
            {
                var dot = size * 0.42f;
                using var amber = new SolidBrush(Warning);
                using var ring = new Pen(Background, Math.Max(1f, size / 16f));
                g.FillEllipse(amber, size - dot, size - dot, dot - 0.5f, dot - 0.5f);
                g.DrawEllipse(ring, size - dot, size - dot, dot - 0.5f, dot - 0.5f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Color FromHue(double hue)
    {
        var x = 1 - Math.Abs(hue / 60 % 2 - 1);
        (double R, double G, double B) rgb = (hue / 60) switch
        {
            < 1 => (1, x, 0),
            < 2 => (x, 1, 0),
            < 3 => (0, 1, x),
            < 4 => (0, x, 1),
            < 5 => (x, 0, 1),
            _ => (1, 0, x),
        };
        var (r, g, b) = rgb;
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    private sealed class Renderer() : ToolStripProfessionalRenderer(new Colors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : DimText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Accent;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A cyan bar instead of the default check box.
            var bounds = e.ImageRectangle;
            using var brush = new SolidBrush(Accent);
            e.Graphics.FillRectangle(brush, bounds.Left + bounds.Width / 2 - 2, bounds.Top + 2, 4, bounds.Height - 4);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;

        public override Color ImageMarginGradientBegin => Background;

        public override Color ImageMarginGradientMiddle => Background;

        public override Color ImageMarginGradientEnd => Background;

        public override Color MenuBorder => Border;

        public override Color MenuItemBorder => Accent;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color MenuItemPressedGradientBegin => Panel;

        public override Color MenuItemPressedGradientEnd => Panel;

        public override Color CheckBackground => Background;

        public override Color CheckSelectedBackground => Hover;

        public override Color CheckPressedBackground => Hover;

        public override Color SeparatorDark => Border;

        public override Color SeparatorLight => Border;
    }
}

internal enum TrayIconState
{
    Ok,
    Degraded,
    Off,
}
