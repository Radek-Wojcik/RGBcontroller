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
    public static Icon CreateIcon(TrayIconState state)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var stroke = Math.Max(2f, size / 6f);
            var rect = new RectangleF(stroke / 2 + 0.5f, stroke / 2 + 0.5f, size - stroke - 1, size - stroke - 1);
            switch (state)
            {
                case TrayIconState.Ok:
                    const int Segments = 24;
                    for (var i = 0; i < Segments; i++)
                    {
                        using var pen = new Pen(FromHue(i * 360.0 / Segments), stroke);
                        g.DrawArc(pen, rect, -90 + i * 360f / Segments, 360f / Segments + 1);
                    }

                    break;
                case TrayIconState.Degraded:
                    using (var pen = new Pen(Warning, stroke))
                    {
                        g.DrawArc(pen, rect, -60, 300);
                    }

                    break;
                default:
                    using (var pen = new Pen(DimText, stroke))
                    {
                        g.DrawEllipse(pen, rect);
                    }

                    break;
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
