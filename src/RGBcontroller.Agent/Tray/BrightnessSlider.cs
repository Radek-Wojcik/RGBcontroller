using System.Globalization;

namespace RGBcontroller.Agent.Tray;

/// <summary>
/// Owner-drawn 0-100 slider for the tray menu (SPEC 7.1): a native TrackBar cannot be themed dark. Mouse drag, wheel
/// and arrow keys (Page Up/Down by 10) change the value; the number on the right is mono-spaced.
/// </summary>
internal sealed class BrightnessSlider : Control
{
    private const int TrackHeightLogical = 4;
    private const int ThumbWidthLogical = 6;
    private const int LabelGapLogical = 10;
    private int _value;
    private bool _dragging;

    public BrightnessSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = TrayTheme.Background;
        Font = new Font("Consolas", 9f);
        AccessibleName = "Strip brightness";
        AccessibleRole = AccessibleRole.Slider;
    }

    /// <summary>Raised while the user changes the value (not when <see cref="Value"/> is set by code).</summary>
    public event EventHandler? ValueChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped != _value)
            {
                _value = clamped;
                AccessibleDescription = $"{clamped}%";
                Invalidate();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var track = TrackBounds;
        using (var background = new SolidBrush(TrayTheme.Border))
        {
            g.FillRectangle(background, track);
        }

        var filled = (int)Math.Round(track.Width * _value / 100.0);
        using (var accent = new SolidBrush(TrayTheme.Accent))
        {
            g.FillRectangle(accent, track.Left, track.Top, filled, track.Height);
            var thumbWidth = LogicalToDeviceUnits(ThumbWidthLogical);
            var reach = LogicalToDeviceUnits(6);
            var thumb = new Rectangle(track.Left + filled - thumbWidth / 2, track.Top - reach, thumbWidth, track.Height + 2 * reach);
            g.FillRectangle(Focused || _dragging ? accent : Brushes.White, thumb);
        }

        TextRenderer.DrawText(g, _value.ToString(CultureInfo.InvariantCulture) + "%", Font,
            new Rectangle(Width - LabelWidth, 0, LabelWidth, Height), TrayTheme.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            Capture = true;
            SetFromMouse(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetFromMouse(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Change(e.Delta > 0 ? 5 : -5);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyCode switch
        {
            Keys.Left or Keys.Down => -1,
            Keys.Right or Keys.Up => 1,
            Keys.PageDown => -10,
            Keys.PageUp => 10,
            Keys.Home => -100,
            Keys.End => 100,
            _ => 0,
        };
        if (step != 0)
        {
            Change(step);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <summary>
    /// Room for "100%" in the current font and DPI plus a gap; a fixed 44 px let the label run into the track on the
    /// owner's 4K monitor with display scaling (stage 4 gate).
    /// </summary>
    private int LabelWidth => TextRenderer.MeasureText("100%", Font, Size.Empty, TextFormatFlags.NoPadding).Width + LogicalToDeviceUnits(LabelGapLogical);

    private Rectangle TrackBounds
    {
        get
        {
            var thumb = LogicalToDeviceUnits(ThumbWidthLogical);
            var height = LogicalToDeviceUnits(TrackHeightLogical);
            return new(thumb, Height / 2 - height / 2, Math.Max(1, Width - LabelWidth - thumb * 2), height);
        }
    }

    private void SetFromMouse(int x)
    {
        var track = TrackBounds;
        Change((int)Math.Round((x - track.Left) * 100.0 / track.Width) - _value);
    }

    private void Change(int delta)
    {
        var before = _value;
        Value = _value + delta;
        if (_value != before)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
