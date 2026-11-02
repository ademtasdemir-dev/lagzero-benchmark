using Lagzero.Core;

namespace Lagzero.UI;

/// <summary>Plain-language evaluation: summary (Fazit) or one section with traffic light, rating, basis, explanations and tips. Auto height.</summary>
internal sealed class EvalView : LzControl
{
    public static readonly Color Green = Color.FromArgb(46, 196, 107);
    public static readonly Color Yellow = Color.FromArgb(242, 194, 48);

    public static Color LightColor(Light l) => l switch
    {
        Light.Green => Green, Light.Yellow => Yellow, Light.Red => Theme.RedHot, _ => Theme.SilverDark,
    };

    private SectionEval? _sec;
    private Evaluation? _fazit;
    private bool _sizing;

    public EvalView() { BackColor = Theme.Panel; }

    public void SetSection(SectionEval s) { _sec = s; _fazit = null; UpdateHeight(); Invalidate(); }
    public void SetFazit(Evaluation e) { _fazit = e; _sec = null; UpdateHeight(); Invalidate(); }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateHeight(); }

    private void UpdateHeight()
    {
        if (Width <= 0 || _sizing) return;
        _sizing = true;
        try
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            int h = (int)Math.Ceiling(Layout(g, paint: false));
            if (Math.Abs(Height - h) > 1) Height = h;
        }
        finally { _sizing = false; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Hq(e.Graphics);
        e.Graphics.Clear(BackColor);
        Layout(e.Graphics, paint: true);
    }

    /// <summary>Measures (and optionally draws) the content; returns the total height.</summary>
    private float Layout(Graphics g, bool paint)
    {
        float s = S, y = 2 * s, x0 = 4 * s, w = Math.Max(60, Width - 8 * s);
        using var body = Theme.Ui(9.5f);
        using var bodyB = Theme.Ui(9.5f, FontStyle.Bold);
        using var small = Theme.Ui(8.5f);
        using var big = Theme.Ui(11f);
        using var head = Theme.Display(8.5f, FontStyle.Bold);
        using var rateF = Theme.Display(10f, FontStyle.Bold);
        using var silver = new SolidBrush(Theme.Silver);
        using var light = new SolidBrush(Theme.SilverLight);
        using var dim = new SolidBrush(Theme.SilverDim);
        using var red = new SolidBrush(Theme.Red);

        float Text(string t, Font f, Brush b, float x, float width, float after = 3)
        {
            var sz = g.MeasureString(t, f, (int)Math.Max(10, width));
            if (paint) g.DrawString(t, f, b, new RectangleF(x, y, width, sz.Height + 2));
            y += sz.Height + after * s;
            return sz.Height;
        }

        if (_fazit != null)
        {
            foreach (var line in _fazit.Fazit)
            {
                if (paint) g.FillRectangle(red, x0, y + 7 * s, 5 * s, 5 * s);
                Text(line, big, light, x0 + 16 * s, w - 16 * s, 5);
            }
            y += 2 * s;
            Text($"Bewertet wird nur mit nachvollziehbaren Grundlagen (Schnittstellen-, Takt- und Herstellerangaben, Bildwiederholrate) – alle Grenzen stehen in bewertung.json neben der Programmdatei. " +
                 "Ohne Vergleichsdatenbank gibt es keinen Vergleich mit anderen PCs.", small, dim, x0, w);
            if (_fazit.RulesError != null) Text("⚠ " + _fazit.RulesError, small, red, x0, w);
            return y + 4 * s;
        }
        if (_sec == null) return 20 * s;
        var sec = _sec;

        // traffic light + rating
        float d = 18 * s;
        if (paint)
        {
            var lc = LightColor(sec.Light);
            using var glow = new SolidBrush(Color.FromArgb(60, lc));
            g.FillEllipse(glow, x0 - 3 * s, y - 3 * s, d + 6 * s, d + 6 * s);
            using var lb = new SolidBrush(lc);
            g.FillEllipse(lb, x0, y, d, d);
            using var rb = new SolidBrush(sec.Light == Light.Gray ? Theme.SilverDim : Theme.SilverLight);
            Theme.DrawTracked(g, sec.Rating.ToUpperInvariant(), rateF, rb, x0 + d + 12 * s, y + 1 * s, 1.5f * s);
        }
        y += d + 10 * s;

        // explanations
        foreach (var l in sec.Lines)
        {
            float valW = Math.Min(w * 0.55f, g.MeasureString(l.Value, bodyB).Width + 6 * s);
            var labelSz = g.MeasureString(l.Label, body, (int)Math.Max(10, w - valW - 10 * s));
            if (paint)
            {
                g.DrawString(l.Label, body, silver, new RectangleF(x0, y, w - valW - 10 * s, labelSz.Height + 2));
                using var rf = new StringFormat { Alignment = StringAlignment.Far };
                g.DrawString(l.Value, bodyB, light, new RectangleF(x0 + w - valW, y, valW, labelSz.Height + 20 * s), rf);
            }
            y += Math.Max(labelSz.Height, g.MeasureString(l.Value, bodyB, (int)valW).Height) + 1 * s;
            Text("Was heißt das? " + l.Meaning, small, dim, x0 + 10 * s, w - 10 * s, 7);
        }

        // tips
        foreach (var t in sec.Tips)
        {
            float top = y;
            if (paint) Theme.DrawTracked(g, "TIPP", head, red, x0, y + 2 * s, 1.2f * s);
            y = top;
            Text(t, body, silver, x0 + 44 * s, w - 44 * s, 6);
        }

        // basis
        y += 4 * s;
        if (paint) Theme.DrawTracked(g, "GRUNDLAGE DER BEWERTUNG", head, dim, x0, y, 1.4f * s);
        y += 16 * s;
        foreach (var b in sec.Basis) Text(b, small, dim, x0, w, 4);
        return y + 4 * s;
    }
}
