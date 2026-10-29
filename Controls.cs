using System.Drawing.Drawing2D;

namespace Brilho;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(32, 32, 32);
    public static readonly Color Card = Color.FromArgb(45, 45, 45);
    public static readonly Color Line = Color.FromArgb(60, 60, 60);
    public static readonly Color Fg = Color.FromArgb(242, 242, 242);
    public static readonly Color Dim = Color.FromArgb(160, 160, 160);
    public static readonly Color Accent = Color.FromArgb(96, 205, 255);
    public static readonly Color Software = Color.FromArgb(255, 196, 92);
}

/// Controle deslizante plano no estilo do Windows 11.
sealed class FlatSlider : Control
{
    double current;
    public bool Dragging { get; private set; }
    /// Só quando a pessoa mexe; atribuir `Value` não dispara.
    public event Action<double> UserChanged;

    public FlatSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public double Value
    {
        get => current;
        set
        {
            current = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    float Radius => Height * 0.3f;
    float Left0 => Radius + 2;
    float Right0 => Width - Radius - 2;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float cy = Height / 2f, thickness = Math.Max(3f, Height * 0.13f);
        float x = Left0 + (float)current * (Right0 - Left0);
        using (var track = new Pen(Theme.Line, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, Left0, cy, Right0, cy);
        if (x > Left0 + 0.5f)
            using (var fill = new Pen(Theme.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(fill, Left0, cy, x, cy);
        float r = Radius;
        using (var thumb = new SolidBrush(Color.FromArgb(69, 69, 69))) g.FillEllipse(thumb, x - r, cy - r, r * 2, r * 2);
        float inner = r * (Dragging ? 0.62f : 0.5f);
        using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, x - inner, cy - inner, inner * 2, inner * 2);
    }

    void SetUser(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (Math.Abs(value - current) < 0.0005) return;
        current = value;
        Invalidate();
        UserChanged?.Invoke(value);
    }

    void SetFromX(int x) => SetUser((x - Left0) / Math.Max(1f, Right0 - Left0));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Dragging = true;
        Capture = true;
        SetFromX(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetUser(current + Math.Sign(e.Delta) * 0.02);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: SetUser(current - 0.01); break;
            case Keys.Right or Keys.Up: SetUser(current + 0.01); break;
            case Keys.PageDown: SetUser(current - 0.1); break;
            case Keys.PageUp: SetUser(current + 0.1); break;
        }
    }
}

/// Chave liga/desliga no estilo do Windows 11.
sealed class ToggleSwitch : Control
{
    bool on;
    public event Action<bool> Toggled;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public bool Checked
    {
        get => on;
        set
        {
            on = value;
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        on = !on;
        Invalidate();
        Toggled?.Invoke(on);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float h = Height - 2, w = Width - 2, r = h / 2;
        using var track = new GraphicsPath();
        track.AddArc(1, 1, h, h, 90, 180);
        track.AddArc(1 + w - h, 1, h, h, 270, 180);
        track.CloseFigure();
        if (on)
            using (var fill = new SolidBrush(Theme.Accent)) g.FillPath(fill, track);
        else
            using (var border = new Pen(Theme.Dim, Math.Max(1f, h / 14))) g.DrawPath(border, track);
        float knob = r * 0.55f, cx = on ? 1 + w - r : 1 + r, cy = 1 + r;
        using var brush = new SolidBrush(on ? Theme.Bg : Theme.Dim);
        g.FillEllipse(brush, cx - knob, cy - knob, knob * 2, knob * 2);
    }
}

/// O indicador que aparece ao usar os atalhos, como o BrightnessOSD do Vorssaint.
sealed class Osd : Form
{
    readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 1400 };
    string title = "", detail = "";
    double level;
    float scale = 1;
    Font titleFont, detailFont;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(28, 28, 28);
        TopMost = true;
        DoubleBuffered = true;
        Opacity = 0.96;
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    int S(float v) => (int)Math.Round(v * scale);

    public void ShowFor(DisplayState state)
    {
        var b = state.Info.Bounds;
        float newScale = Dpi.ScaleAt(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
        if (titleFont == null || Math.Abs(newScale - scale) > 0.01f)
        {
            scale = newScale;
            titleFont?.Dispose();
            detailFont?.Dispose();
            titleFont = new Font("Segoe UI Semibold", 14 * scale, GraphicsUnit.Pixel);
            detailFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        }
        title = state.Info.FriendlyName;
        detail = state.MethodLabel;
        level = state.Level;
        int w = S(300), h = S(78);
        Bounds = new Rectangle(b.X + (b.Width - w) / 2, b.Bottom - h - b.Height / 9, w, h);
        if (!Visible) Show();
        Native.KeepOnTop(Handle);
        Invalidate();
        hideTimer.Stop();
        hideTimer.Start();
    }

    public void KeepAbove()
    {
        if (Visible) Native.KeepOnTop(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int pad = S(16);
        string pct = $"{Math.Round(level * 100)}%";
        TextRenderer.DrawText(g, title, titleFont, new Rectangle(pad, S(10), Width - pad * 2 - S(60), S(22)), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, pct, titleFont, new Rectangle(Width - pad - S(60), S(10), S(60), S(22)), Theme.Fg,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
        float y = S(44), x0 = pad, x1 = Width - pad, thickness = S(6);
        using (var track = new Pen(Theme.Line, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, x0, y, x1, y);
        if (level > 0.004)
            using (var fill = new Pen(Theme.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(fill, x0, y, x0 + (float)level * (x1 - x0), y);
        TextRenderer.DrawText(g, detail, detailFont, new Rectangle(pad, S(54), Width - pad * 2, S(18)), Theme.Dim,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

static class AppIcon
{
    /// Um sol desenhado em código, para não depender de arquivo de ícone.
    public static Icon Create(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = Color.FromArgb(255, 205, 70);
            float c = size / 2f, core = size * 0.2f;
            using var pen = new Pen(color, Math.Max(2f, size / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                float inner = core * 1.55f, outer = core * 2.3f;
                g.DrawLine(pen, c + (float)Math.Cos(a) * inner, c + (float)Math.Sin(a) * inner,
                    c + (float)Math.Cos(a) * outer, c + (float)Math.Sin(a) * outer);
            }
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, c - core, c - core, core * 2, core * 2);
        }
        IntPtr handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Native.DestroyIcon(handle);
        return icon;
    }
}
