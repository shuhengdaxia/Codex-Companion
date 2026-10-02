using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Shared light presentation for the native desktop controls.
internal static class DesktopPalette
{
    internal static readonly Color Page = Color.FromArgb(249, 250, 252);
    internal static readonly Color Surface = Color.White;
    internal static readonly Color Input = Color.White;
    internal static readonly Color Border = Color.FromArgb(220, 226, 234);
    internal static readonly Color Text = Color.FromArgb(28, 37, 50);
    internal static readonly Color Muted = Color.FromArgb(98, 110, 126);
    internal static readonly Color Accent = Color.FromArgb(23, 113, 230);
    internal static readonly Color Selected = Color.FromArgb(234, 244, 255);

    internal static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color, StringAlignment alignment)
    {
        // GDI+ respects pixel fonts on both the display and DrawToBitmap.
        // TextRenderer's cached HFONT can use the desktop DPI for a bitmap DC.
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            graphics.DrawString(text, font, brush, bounds, format);
    }

    internal static GraphicsPath Round(RectangleF rect, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        if (diameter <= 0) return path;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void LightTitleBar(Form form)
    {
        if (Environment.OSVersion.Version.Major < 6) return;
        try
        {
            int enabled = 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, 4) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, 4);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    internal static bool AnimationsEnabled
    {
        get
        {
            int enabled;
            // SPI_GETCLIENTAREAANIMATION is also available on .NET Framework.
            return SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
}

internal class DesktopSurface : Panel
{
    public Color BorderColor = DesktopPalette.Border;
    public int CornerRadius = 14;
    public DesktopSurface()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = DesktopPalette.Surface;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var background = new SolidBrush(Parent == null ? DesktopPalette.Page : Parent.BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = DesktopPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius))
        using (var fill = new SolidBrush(BackColor))
            e.Graphics.FillPath(fill, path);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = DesktopPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius))
        using (var pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class DesktopButton : Button
{
    private bool hovered;
    private bool pressed;
    private float hoverAmount;
    private readonly Timer hoverTimer = new Timer { Interval = 16 };
    public bool Primary;
    public bool Borderless;
    public int CornerRadius = 10;
    public Color AccentColor = DesktopPalette.Accent;
    public Color BorderColor = DesktopPalette.Border;
    public DesktopButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = DesktopPalette.Surface;
        ForeColor = DesktopPalette.Text;
        Cursor = Cursors.Hand;
        hoverTimer.Tick += delegate {
            float target = hovered && Enabled ? 1 : 0;
            hoverAmount += (target - hoverAmount) * .35F;
            if (Math.Abs(target - hoverAmount) < .02F) { hoverAmount = target; hoverTimer.Stop(); }
            Invalidate();
        };
    }
    private void UpdateHover()
    {
        if (!Enabled || !DesktopPalette.AnimationsEnabled)
        {
            hoverTimer.Stop();
            hoverAmount = hovered && Enabled ? 1 : 0;
            Invalidate();
        }
        else hoverTimer.Start();
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; UpdateHover(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; UpdateHover(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { pressed = false; UpdateHover(); Invalidate(); base.OnEnabledChanged(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) hoverTimer.Dispose();
        base.Dispose(disposing);
    }
    private static Color Blend(Color from, Color to, float amount)
    {
        return Color.FromArgb((int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using (var background = new SolidBrush(Parent == null ? DesktopPalette.Surface : Parent.BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Blend(Primary ? AccentColor : BackColor,
            Primary ? Color.FromArgb(10, 99, 213) : Color.FromArgb(237, 244, 253), hoverAmount);
        Color ink = Primary ? Color.White : ForeColor;
        if (!Enabled) { fill = Color.FromArgb(237, 240, 244); ink = Color.FromArgb(137, 148, 162); }
        else if (pressed) fill = Primary ? Color.FromArgb(13, 89, 190) : Color.FromArgb(229, 239, 251);
        using (var path = DesktopPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(Focused && Enabled ? AccentColor : Primary && Enabled ? fill : BorderColor))
        { e.Graphics.FillPath(brush, path); if (!Borderless) e.Graphics.DrawPath(pen, path); }
        var bounds = new Rectangle(6, pressed ? 1 : 0, Math.Max(0, Width - 12), Height);
        DesktopPalette.DrawText(e.Graphics, Text, Font, bounds, ink, StringAlignment.Center);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
    }
}

internal sealed class DesktopComboBox : ComboBox
{
    public DesktopComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 28;
        BackColor = DesktopPalette.Input;
        ForeColor = DesktopPalette.Text;
        IntegralHeight = false;
        DropDownHeight = 280;
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Retain native popup/keyboard behavior while painting the closed
        // selector consistently on the display and in DrawToBitmap previews.
        if (m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318)
        {
            using (Graphics graphics = m.Msg == 0x000F || m.WParam == IntPtr.Zero
                ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
                PaintSelector(graphics);
        }
    }
    private void PaintSelector(Graphics graphics)
    {
        if (Width < 2 || Height < 2) return;
        using (var background = new SolidBrush(Parent == null ? DesktopPalette.Surface : Parent.BackColor))
            graphics.FillRectangle(background, ClientRectangle);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = DesktopPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), 7))
        using (var fill = new SolidBrush(BackColor))
        using (var pen = new Pen(Focused ? DesktopPalette.Accent : DesktopPalette.Border))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(pen, path);
        }
        DesktopPalette.DrawText(graphics, SelectedItem == null ? Text : GetItemText(SelectedItem), Font,
            new Rectangle(10, 0, Math.Max(0, Width - 40), Height), Enabled ? ForeColor : DesktopPalette.Muted,
            StringAlignment.Near);
        int x = Width - 20, y = Height / 2;
        using (var pen = new Pen(DesktopPalette.Muted, 1.4F))
            graphics.DrawLines(pen, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var brush = new SolidBrush(selected ? DesktopPalette.Selected : BackColor))
            e.Graphics.FillRectangle(brush, e.Bounds);
        DesktopPalette.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height),
            Enabled ? ForeColor : DesktopPalette.Muted, StringAlignment.Near);
        e.DrawFocusRectangle();
    }
}

// A quiet rendering of the orbit motif from the mid-web landing page.
// It is decorative artwork, never an indicator of connection health.
internal sealed class OrbitArtwork : Control
{
    internal OrbitArtwork()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Mid-web 轨道装饰";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = Width * .55F, cy = Height * .5F;
        e.Graphics.TranslateTransform(cx, cy);
        using (var faint = new Pen(Color.FromArgb(38, 62, 48)))
        using (var bright = new Pen(DesktopPalette.Accent, 1.2F))
        {
            for (int i = 0; i < 3; i++)
            {
                var state = e.Graphics.Save();
                e.Graphics.RotateTransform(-24 + i * 52);
                e.Graphics.DrawEllipse(faint, -72, -25, 144, 50);
                e.Graphics.DrawArc(bright, -72, -25, 144, 50, 8 + i * 44, 46);
                e.Graphics.Restore(state);
            }
            using (var brush = new SolidBrush(DesktopPalette.Accent))
                e.Graphics.FillEllipse(brush, -4, -4, 8, 8);
        }
        e.Graphics.ResetTransform();
    }
}
