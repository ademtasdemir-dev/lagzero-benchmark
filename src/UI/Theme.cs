using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace Lagzero.UI;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(8, 8, 9);
    public static readonly Color Panel = Color.FromArgb(18, 18, 20);
    public static readonly Color PanelHi = Color.FromArgb(28, 28, 31);
    public static readonly Color Border = Color.FromArgb(44, 44, 48);
    public static readonly Color Silver = Color.FromArgb(206, 210, 216);
    public static readonly Color SilverLight = Color.FromArgb(242, 244, 247);
    public static readonly Color SilverDim = Color.FromArgb(128, 133, 140);
    public static readonly Color SilverDark = Color.FromArgb(70, 73, 78);
    public static readonly Color Red = Color.FromArgb(225, 6, 0);
    public static readonly Color RedHot = Color.FromArgb(255, 45, 35);
    public static readonly Color RedDark = Color.FromArgb(110, 8, 6);
    public static readonly Color Ok = Color.FromArgb(206, 210, 216);

    public static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static readonly string DisplayFamily = FontExists("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
    private static readonly string MonoFamily = FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    public static Font Display(float pt, FontStyle st = FontStyle.Bold) => new(DisplayFamily, pt, st);
    public static Font Ui(float pt, FontStyle st = FontStyle.Regular) => new("Segoe UI", pt, st);
    public static Font Mono(float pt, FontStyle st = FontStyle.Regular) => new(MonoFamily, pt, st);

    private static bool FontExists(string name)
    {
        using var fc = new InstalledFontCollection();
        return fc.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static void Hq(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (rad <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static LinearGradientBrush SilverBrush(RectangleF r) =>
        new(new PointF(r.X, r.Y), new PointF(r.X, r.Bottom + 1),
            SilverLight, Color.FromArgb(150, 155, 162))
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { SilverLight, Color.FromArgb(214, 218, 224), Color.FromArgb(140, 145, 152), Color.FromArgb(200, 204, 210) },
                Positions = new[] { 0f, 0.45f, 0.55f, 1f },
            },
        };

    /// <summary>Draws text with extra letter spacing (tracking) in px; returns the drawn width.</summary>
    public static float DrawTracked(Graphics g, string text, Font f, Brush b, float x, float y, float tracking)
    {
        float start = x;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        foreach (char ch in text)
        {
            string s = ch.ToString();
            g.DrawString(s, f, b, x, y, fmt);
            x += g.MeasureString(s, f, PointF.Empty, fmt).Width + tracking;
        }
        return x - start - tracking;
    }

    public static float MeasureTracked(Graphics g, string text, Font f, float tracking)
    {
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        float w = 0;
        foreach (char ch in text) w += g.MeasureString(ch.ToString(), f, PointF.Empty, fmt).Width + tracking;
        return w - tracking;
    }

    public static string Ms(double v) => v.ToString("0.000", De) + " ms";
    public static string Hz(double v) => v.ToString("N0", De) + " Hz";
}

/// <summary>Base for flicker-free custom painted controls.</summary>
internal class LzControl : Control
{
    protected float S => DeviceDpi / 96f;

    public LzControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Silver;
    }
}
