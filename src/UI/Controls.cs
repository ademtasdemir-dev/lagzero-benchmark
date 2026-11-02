using System.Drawing.Drawing2D;

namespace Lagzero.UI;

/// <summary>LAG|ZERØ header with slogan.</summary>
internal sealed class HeaderView : LzControl
{
    public string RightTitle { get; set; } = "CONTROLLER BENCHMARK";
    public string RightSub { get; set; } = "";

    public HeaderView() { BackColor = Theme.Bg; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        // subtle top gradient
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(22, 22, 25), Theme.Bg, 90f))
            g.FillRectangle(bg, ClientRectangle);

        float x = 22 * s, y = 10 * s;
        float em = 40 * s;
        using var family = new FontFamily(Theme.Display(10).FontFamily.Name);
        var parts = new[] { ("LAG", true), ("|", false), ("ZERØ", true) };
        using var fmt = StringFormat.GenericTypographic;
        foreach (var (txt, silver) in parts)
        {
            using var path = new GraphicsPath();
            path.AddString(txt, family, (int)FontStyle.Bold, em, new PointF(x, y), fmt);
            var b = path.GetBounds();
            if (silver)
            {
                using var br = Theme.SilverBrush(new RectangleF(b.X, y + em * 0.12f, b.Width, em * 0.78f));
                g.FillPath(br, path);
                using var pen = new Pen(Color.FromArgb(90, 0, 0, 0), 1f * s);
                g.DrawPath(pen, path);
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(70, Theme.Red), 6 * s) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                using var br = new SolidBrush(Theme.Red);
                g.FillPath(br, path);
            }
            x = b.Right + (silver ? 6 : 8) * s;
        }

        using (var slogan = Theme.Display(9.5f, FontStyle.Bold))
        using (var red = new SolidBrush(Theme.Red))
            Theme.DrawTracked(g, "TESTED. MEASURED. OPTIMIZED.", slogan, red, 25 * s, y + em * 1.12f, 3.2f * s);

        // right block
        using (var tf = Theme.Display(15, FontStyle.Bold))
        using (var sf = Theme.Ui(8.5f))
        using (var silverB = new SolidBrush(Theme.Silver))
        using (var dimB = new SolidBrush(Theme.SilverDim))
        {
            float tw = Theme.MeasureTracked(g, RightTitle, tf, 2.5f * s);
            float rx = Width - 24 * s - tw;
            Theme.DrawTracked(g, RightTitle, tf, silverB, rx, 22 * s, 2.5f * s);
            var sz = g.MeasureString(RightSub, sf);
            g.DrawString(RightSub, sf, dimB, Width - 24 * s - sz.Width, 52 * s);
        }

        // bottom red rule fading out
        float ly = Height - 3 * s;
        using (var rule = new LinearGradientBrush(new RectangleF(0, ly, Width, 3 * s), Theme.Red, Color.FromArgb(0, Theme.Red), 0f))
        {
            rule.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Theme.Red, Theme.Red, Color.FromArgb(120, Theme.RedDark), Color.FromArgb(0, Theme.Bg) },
                Positions = new[] { 0f, 0.35f, 0.75f, 1f },
            };
            g.FillRectangle(rule, 0, ly, Width, 2 * s);
        }
    }
}

/// <summary>Panel with a tracked caption and thin border.</summary>
internal sealed class Card : Panel
{
    public string Title { get; set; }
    public string? Badge { get; set; }
    private float S => DeviceDpi / 96f;

    public Card(string title)
    {
        Title = title;
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        ForeColor = Theme.Silver;
        Margin = new Padding(6);
        ResizeRedraw = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int s = (int)Math.Round(S);
        Padding = new Padding((int)(12 * S), (int)(34 * S), (int)(12 * S), (int)(10 * S));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        using (var pen = new Pen(Theme.Border, 1))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using (var red = new SolidBrush(Theme.Red))
            g.FillRectangle(red, 0, 0, 3 * s, 22 * s + 6 * s);
        using var f = Theme.Display(9.5f, FontStyle.Bold);
        using var b = new SolidBrush(Theme.Silver);
        float w = Theme.DrawTracked(g, Title, f, b, 13 * s, 9 * s, 2f * s);
        if (Badge != null)
        {
            using var bf = Theme.Ui(8f, FontStyle.Bold);
            var sz = g.MeasureString(Badge, bf);
            var r = new RectangleF(Width - sz.Width - 22 * s, 7 * s, sz.Width + 10 * s, sz.Height + 2 * s);
            using var path = Theme.Round(r, 3 * s);
            using var rb = new SolidBrush(Theme.RedDark);
            g.FillPath(rb, path);
            using var tb = new SolidBrush(Theme.SilverLight);
            g.DrawString(Badge, bf, tb, r.X + 5 * s, r.Y + 1 * s);
        }
    }
}

/// <summary>Flat button: silver outline, red when primary / hovered.</summary>
internal sealed class LzButton : LzControl
{
    public bool Primary { get; set; }
    private bool _hover, _down;

    public LzButton(string text, bool primary = false)
    {
        Text = text; Primary = primary;
        Cursor = Cursors.Hand;
        BackColor = Theme.Panel;
        Margin = new Padding(4);
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, 3 * s);
        Color fill, border, text;
        if (!Enabled) { fill = Theme.Panel; border = Theme.SilverDark; text = Theme.SilverDark; }
        else if (Primary)
        {
            fill = _down ? Theme.RedDark : _hover ? Theme.RedHot : Theme.Red;
            border = _hover ? Theme.RedHot : Theme.Red; text = Color.White;
        }
        else
        {
            fill = _down ? Theme.Border : _hover ? Theme.PanelHi : Theme.Panel;
            border = _hover ? Theme.Red : Theme.SilverDim; text = _hover ? Theme.SilverLight : Theme.Silver;
        }
        using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
        using (var pen = new Pen(border, 1.2f * s)) g.DrawPath(pen, path);
        using var f = Theme.Display(9.5f, FontStyle.Bold);
        using var tb = new SolidBrush(text);
        float tw = Theme.MeasureTracked(g, Text, f, 1.5f * s);
        var th = g.MeasureString("X", f).Height;
        Theme.DrawTracked(g, Text, f, tb, (Width - tw) / 2f, (Height - th) / 2f + 1 * s, 1.5f * s);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(Color.FromArgb(120, Theme.SilverLight), 1) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(fp, 3 * s, 3 * s, Width - 7 * s, Height - 7 * s);
        }
    }
}

/// <summary>Thin progress bar with status text.</summary>
internal sealed class ProgressView : LzControl
{
    private double _value;
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); Invalidate(); } }
    public string Status { get; set; } = "BEREIT";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Panel);
        using var f = Theme.Ui(8.5f, FontStyle.Bold);
        using var tb = new SolidBrush(Theme.SilverDim);
        g.DrawString(Status, f, tb, 0, 0);
        float y = Height - 6 * s;
        using (var bb = new SolidBrush(Theme.Border)) g.FillRectangle(bb, 0, y, Width, 4 * s);
        if (_value > 0)
            using (var rb = new LinearGradientBrush(new RectangleF(0, y, Math.Max(1, Width), 4 * s), Theme.RedDark, Theme.RedHot, 0f))
                g.FillRectangle(rb, 0, y, (float)(Width * _value), 4 * s);
    }
}
