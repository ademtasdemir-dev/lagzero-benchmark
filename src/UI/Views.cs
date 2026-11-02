using System.Drawing.Drawing2D;

namespace Lagzero.UI;

/// <summary>Live sticks, triggers and buttons.</summary>
internal sealed class LiveView : LzControl
{
    private XInputState _state;
    private bool _connected;

    public void SetState(XInputState s, bool connected)
    {
        bool changed = connected != _connected || s.PacketNumber != _state.PacketNumber;
        _state = s; _connected = connected;
        if (changed) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Panel);
        using var small = Theme.Ui(8f);
        using var smallB = Theme.Ui(8f, FontStyle.Bold);
        using var mono = Theme.Mono(8f);
        using var dim = new SolidBrush(Theme.SilverDim);
        using var silver = new SolidBrush(Theme.Silver);

        if (!_connected)
        {
            using var f = Theme.Display(11);
            using var rb = new SolidBrush(Theme.Red);
            var msg = "KEIN CONTROLLER";
            var sz = g.MeasureString(msg, f);
            g.DrawString(msg, f, rb, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
            return;
        }

        var gp = _state.Gamepad;
        float triggerW = 16 * s;
        float gap = 14 * s;
        float stickArea = Width - 2 * triggerW - 4 * gap;
        float dia = Math.Min(stickArea / 2 - gap, Height * 0.52f);
        dia = Math.Max(dia, 40 * s);
        float top = 18 * s;
        float lxC = triggerW + 2 * gap + dia / 2;
        float rxC = Width - triggerW - 2 * gap - dia / 2;
        DrawStick(g, "L", lxC, top + dia / 2, dia, gp.LX, gp.LY, (gp.Buttons & (ushort)XButtons.LeftThumb) != 0, small, mono, dim, silver);
        DrawStick(g, "R", rxC, top + dia / 2, dia, gp.RX, gp.RY, (gp.Buttons & (ushort)XButtons.RightThumb) != 0, small, mono, dim, silver);
        DrawTrigger(g, "LT", gap * 0.6f, top, triggerW, dia, gp.LeftTrigger, smallB, dim);
        DrawTrigger(g, "RT", Width - gap * 0.6f - triggerW, top, triggerW, dia, gp.RightTrigger, smallB, dim);

        // buttons
        float by = top + dia + 40 * s;
        var btns = new (string, XButtons)[]
        {
            ("LB", XButtons.LeftShoulder), ("RB", XButtons.RightShoulder), ("BACK", XButtons.Back), ("START", XButtons.Start),
            ("A", XButtons.A), ("B", XButtons.B), ("X", XButtons.X), ("Y", XButtons.Y),
            ("↑", XButtons.DPadUp), ("↓", XButtons.DPadDown), ("←", XButtons.DPadLeft), ("→", XButtons.DPadRight),
            ("LS", XButtons.LeftThumb), ("RS", XButtons.RightThumb), ("GUIDE", XButtons.Guide),
        };
        int cols = 8;
        float bw = (Width - (cols + 1) * 6 * s) / cols, bh = 22 * s;
        for (int i = 0; i < btns.Length; i++)
        {
            int c = i % cols, r = i / cols;
            var rect = new RectangleF(6 * s + c * (bw + 6 * s), by + r * (bh + 6 * s), bw, bh);
            bool on = (gp.Buttons & (ushort)btns[i].Item2) != 0;
            using var path = Theme.Round(rect, 3 * s);
            using (var fb = new SolidBrush(on ? Theme.Red : Theme.PanelHi)) g.FillPath(fb, path);
            using (var pen = new Pen(on ? Theme.RedHot : Theme.Border, 1)) g.DrawPath(pen, path);
            var sz = g.MeasureString(btns[i].Item1, smallB);
            using var tb = new SolidBrush(on ? Color.White : Theme.SilverDim);
            g.DrawString(btns[i].Item1, smallB, tb, rect.X + (rect.Width - sz.Width) / 2, rect.Y + (rect.Height - sz.Height) / 2);
        }

        string footer = $"PAKET #{_state.PacketNumber:N0}   BTN 0x{gp.Buttons:X4}";
        g.DrawString(footer, mono, dim, 6 * s, Height - 16 * s);
    }

    private void DrawStick(Graphics g, string name, float cx, float cy, float dia, short x, short y, bool click,
        Font small, Font mono, Brush dim, Brush silver)
    {
        float s = S, r = dia / 2;
        var rect = new RectangleF(cx - r, cy - r, dia, dia);
        using (var fb = new SolidBrush(Theme.Bg)) g.FillEllipse(fb, rect);
        using (var pen = new Pen(click ? Theme.Red : Theme.SilverDark, 1.5f * s)) g.DrawEllipse(pen, rect);
        using (var grid = new Pen(Theme.Border, 1))
        {
            g.DrawLine(grid, cx - r, cy, cx + r, cy);
            g.DrawLine(grid, cx, cy - r, cx, cy + r);
            float dz = r * 0.10f; // 10 % reference ring
            using var dzPen = new Pen(Theme.Border, 1) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(dzPen, cx - dz, cy - dz, 2 * dz, 2 * dz);
        }
        double nx = x / 32767.0, ny = y / 32767.0;
        float px = cx + (float)(Math.Clamp(nx, -1, 1) * r), py = cy - (float)(Math.Clamp(ny, -1, 1) * r);
        using (var line = new Pen(Color.FromArgb(120, Theme.Red), 1.2f * s)) g.DrawLine(line, cx, cy, px, py);
        float dr = 6 * s;
        using (var glow = new SolidBrush(Color.FromArgb(70, Theme.Red))) g.FillEllipse(glow, px - dr * 1.8f, py - dr * 1.8f, dr * 3.6f, dr * 3.6f);
        using (var dot = new SolidBrush(Theme.RedHot)) g.FillEllipse(dot, px - dr, py - dr, 2 * dr, 2 * dr);

        g.DrawString(name, small, silver, rect.X, rect.Y - 15 * s);
        string vals = $"X {x,6}  Y {y,6}";
        var sz = g.MeasureString(vals, mono);
        g.DrawString(vals, mono, dim, cx - sz.Width / 2, rect.Bottom + 4 * s);
    }

    private void DrawTrigger(Graphics g, string name, float x, float y, float w, float h, byte v, Font f, Brush dim)
    {
        float s = S;
        var rect = new RectangleF(x, y, w, h);
        using (var bb = new SolidBrush(Theme.Bg)) g.FillRectangle(bb, rect);
        using (var pen = new Pen(Theme.SilverDark, 1)) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        float fh = h * v / 255f;
        if (fh > 0)
            using (var rb = new LinearGradientBrush(rect, Theme.RedHot, Theme.RedDark, 90f))
                g.FillRectangle(rb, x + 1, y + h - fh, w - 1, fh);
        var sz = g.MeasureString(name, f);
        g.DrawString(name, f, dim, x + (w - sz.Width) / 2, y - 15 * s);
        string val = v.ToString();
        var vs = g.MeasureString(val, f);
        g.DrawString(val, f, dim, x + (w - vs.Width) / 2, y + h + 2 * s);
    }
}

/// <summary>Big numbers + filtered / unfiltered table.</summary>
internal sealed class ResultsView : LzControl
{
    private PollingResult? _r;
    public string Hint { get; set; } = "Stick während der Messung gleichmäßig kreisen lassen.";
    public double LiveHz { get; set; }
    public bool Measuring { get; set; }

    public void SetResult(PollingResult? r) { _r = r; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Panel);
        using var dimB = new SolidBrush(Theme.SilverDim);
        using var silverB = new SolidBrush(Theme.Silver);
        using var redB = new SolidBrush(Theme.Red);
        using var cap = Theme.Display(8.5f, FontStyle.Bold);
        using var big = Theme.Display(34, FontStyle.Bold);
        using var mid = Theme.Display(20, FontStyle.Bold);

        var f = _r?.Filtered;
        string hzMed = Measuring ? (LiveHz > 0 ? LiveHz.ToString("N0", Theme.De) : "…") : f != null && f.N > 0 ? f.HzMedian.ToString("N0", Theme.De) : "—";
        string hzMean = !Measuring && f != null && f.N > 0 ? f.HzMean.ToString("N0", Theme.De) : "—";

        float x = 4 * s, y = 0;
        Theme.DrawTracked(g, Measuring ? "LIVE (MEDIAN)" : "POLLING-RATE · MEDIAN", cap, dimB, x, y, 1.6f * s);
        using (var bigB = Theme.SilverBrush(new RectangleF(x, y + 16 * s, 300 * s, 52 * s)))
            g.DrawString(hzMed, big, bigB, x - 4 * s, y + 12 * s);
        float w1 = g.MeasureString(hzMed, big).Width;
        g.DrawString("Hz", mid, redB, x + w1 - 8 * s, y + 31 * s);

        float x2 = Math.Max(x + w1 + 70 * s, Width * 0.46f);
        Theme.DrawTracked(g, "MITTELWERT", cap, dimB, x2, y, 1.6f * s);
        g.DrawString(hzMean, mid, silverB, x2 - 3 * s, y + 24 * s);
        float w2 = g.MeasureString(hzMean, mid).Width;
        using (var sm = Theme.Display(11, FontStyle.Bold)) g.DrawString("Hz", sm, redB, x2 + w2 - 6 * s, y + 33 * s);

        if (_r != null && !Measuring && _r.RateClass > 0)
        {
            string badge = $"{_r.RateClass:N0} HZ-KLASSE".Replace(",", ".");
            using var bf = Theme.Display(9, FontStyle.Bold);
            var sz = g.MeasureString(badge, bf);
            var br = new RectangleF(Width - sz.Width - 18 * s, y + 2 * s, sz.Width + 14 * s, sz.Height + 6 * s);
            using var path = Theme.Round(br, 3 * s);
            using (var pen = new Pen(Theme.Red, 1.4f * s)) g.DrawPath(pen, path);
            g.DrawString(badge, bf, silverB, br.X + 7 * s, br.Y + 3 * s);
        }

        // table
        float ty = y + 82 * s;
        using var head = Theme.Display(8.5f, FontStyle.Bold);
        using var cell = Theme.Mono(10f);
        using var label = Theme.Ui(9.5f);
        float c0 = 4 * s, c1 = Width * 0.45f, c2 = Width * 0.73f;
        float rowH = Math.Max(20 * s, (Height - ty - 46 * s) / 10f);
        Theme.DrawTracked(g, "GEFILTERT", head, redB, c1, ty, 1.5f * s);
        Theme.DrawTracked(g, "UNGEFILTERT", head, dimB, c2, ty, 1.5f * s);
        ty += rowH * 0.9f;
        using (var pen = new Pen(Theme.Border, 1)) g.DrawLine(pen, c0, ty - 4 * s, Width - 4 * s, ty - 4 * s);

        var u = _r?.Unfiltered;
        bool has = _r != null && !Measuring;
        string F(Func<IntervalStats, string> sel, IntervalStats? st) => has && st != null && st.N > 0 ? sel(st) : "—";
        var rows = new (string, Func<IntervalStats, string>)[]
        {
            ("Polling-Rate (Median)", st => Theme.Hz(st.HzMedian)),
            ("Polling-Rate (Mittelwert)", st => Theme.Hz(st.HzMean)),
            ("Abstand Median", st => Theme.Ms(st.MedianMs)),
            ("Abstand min", st => Theme.Ms(st.MinMs)),
            ("Abstand Ø", st => Theme.Ms(st.MeanMs)),
            ("Abstand max", st => Theme.Ms(st.MaxMs)),
            ("Jitter (Std.-Abw.)", st => Theme.Ms(st.JitterMs)),
            ("99-%-Wert", st => Theme.Ms(st.P99Ms)),
            ("Intervalle", st => st.N.ToString("N0", Theme.De)),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            float ry = ty + i * rowH;
            if (i % 2 == 0)
                using (var zb = new SolidBrush(Color.FromArgb(10, 255, 255, 255))) g.FillRectangle(zb, c0 - 4 * s, ry - 3 * s, Width - c0, rowH);
            g.DrawString(rows[i].Item1, label, silverB, c0, ry);
            g.DrawString(F(rows[i].Item2, f), cell, i < 2 ? redB : silverB, c1, ry);
            g.DrawString(F(rows[i].Item2, u), cell, dimB, c2, ry);
        }

        float fy = ty + rows.Length * rowH + 6 * s;
        string foot;
        if (has)
        {
            var c = _r!.Capture;
            foot = $"Ausreißer entfernt: {_r.Outliers:N0}   ·   Übersprungene Pakete: {c.SkippedPackets:N0}   ·   " +
                   $"Abfragen: {c.PollsPerSecond.ToString("N0", Theme.De)}/s   ·   Dauer: {c.DurationSec.ToString("0.00", Theme.De)} s";
            if (c.Error != null) foot += "\n⚠ " + c.Error;
            else if (c.Cancelled) foot += "\n⚠ Messung abgebrochen";
        }
        else foot = Hint;
        using var ff = Theme.Ui(8.5f);
        g.DrawString(foot, ff, dimB, new RectangleF(c0, fy, Width - c0, Height - fy));
    }
}

/// <summary>Interval scatter over time (kept = silver, outliers = red) with median line.</summary>
internal sealed class ChartView : LzControl
{
    private PollingResult? _r;
    public void SetResult(PollingResult? r) { _r = r; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Panel);
        using var small = Theme.Mono(7.5f);
        using var dim = new SolidBrush(Theme.SilverDim);
        float left = 52 * s, right = Width - 6 * s, top = 4 * s, bottom = Height - 16 * s;
        using (var ax = new Pen(Theme.Border, 1))
        {
            g.DrawLine(ax, left, top, left, bottom);
            g.DrawLine(ax, left, bottom, right, bottom);
        }
        if (_r == null || _r.IntervalsMs.Length == 0)
        {
            g.DrawString("Noch keine Messung", small, dim, left + 8 * s, top + 8 * s);
            return;
        }
        var d = _r.IntervalsMs;
        double med = _r.Filtered.MedianMs;
        double yMax = Math.Max(_r.Filtered.P99Ms * 1.6, med * 3);
        double yMin = 0;
        float W = right - left, H = bottom - top;
        float X(int i) => left + (float)i / Math.Max(1, d.Length - 1) * W;
        float Y(double v) => bottom - (float)((Math.Min(v, yMax) - yMin) / (yMax - yMin)) * H;

        using (var grid = new Pen(Color.FromArgb(30, 255, 255, 255), 1) { DashStyle = DashStyle.Dot })
        {
            for (int k = 1; k <= 3; k++)
            {
                double v = yMax * k / 4;
                float yy = Y(v);
                g.DrawLine(grid, left, yy, right, yy);
                g.DrawString(v.ToString("0.000", Theme.De), small, dim, 2 * s, yy - 6 * s);
            }
        }
        g.DrawString("ms", small, dim, 2 * s, top);

        using var keepB = new SolidBrush(Color.FromArgb(150, Theme.Silver));
        using var outB = new SolidBrush(Theme.RedHot);
        float r = Math.Max(1.2f, 1.3f * s);
        for (int i = 0; i < d.Length; i++)
        {
            float px = X(i), py = Y(d[i]);
            if (_r.Keep[i]) g.FillRectangle(keepB, px, py, r, r);
            else g.FillEllipse(outB, px - 2 * s, Math.Max(top, py - 2 * s), 4 * s, 4 * s);
        }
        float my = Y(med);
        using (var mp = new Pen(Theme.Red, 1.4f * s) { DashStyle = DashStyle.Dash }) g.DrawLine(mp, left, my, right, my);
        using (var rb = new SolidBrush(Theme.Red))
            g.DrawString("Median " + med.ToString("0.000", Theme.De), small, rb, right - 90 * s, my - 13 * s);
        g.DrawString($"Paket-Intervalle (n = {d.Length:N0}) · rote Punkte = Ausreißer", small, dim, left, bottom + 2 * s);
    }
}

/// <summary>Drift test result for both sticks.</summary>
internal sealed class DriftView : LzControl
{
    private DriftResult? _r;
    public string Hint { get; set; } = "Sticks loslassen, dann DRIFT-TEST starten (5 s).";
    public void SetResult(DriftResult? r) { _r = r; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Panel);
        using var dim = new SolidBrush(Theme.SilverDim);
        using var silver = new SolidBrush(Theme.Silver);
        using var red = new SolidBrush(Theme.Red);
        using var lab = Theme.Ui(8.5f);
        using var val = Theme.Mono(9.5f);
        using var head = Theme.Display(10, FontStyle.Bold);
        if (_r == null)
        {
            g.DrawString(Hint, lab, dim, 4 * s, 4 * s);
            return;
        }
        float colW = Width / 2f;
        void Col(string name, StickDrift st, float x)
        {
            float y = 0;
            Theme.DrawTracked(g, name, head, silver, x, y, 1.5f * s);
            string verdict = st.Verdict;
            var vb = verdict is "SAUBER" ? silver : red;
            var vs = g.MeasureString(verdict, head);
            g.DrawString(verdict, head, vb, x + colW - vs.Width - 14 * s, y - 2 * s);
            y += 22 * s;
            var lines = new (string, string)[]
            {
                ("Ruhe-Offset", st.OffsetPct.ToString("0.00", Theme.De) + " %"),
                ("Max. Auslenkung", st.MaxRadiusPct.ToString("0.00", Theme.De) + " %"),
                ("Rauschen (σ)", st.NoisePct.ToString("0.000", Theme.De) + " %"),
                ("Deadzone-Empfehlung", st.RecommendedDeadzonePct + " %"),
            };
            float rowH = Math.Max(17 * s, (Height - 24 * s) / 4.3f);
            foreach (var (k, v) in lines)
            {
                g.DrawString(k, lab, dim, x, y);
                g.DrawString(v, val, silver, x + colW * 0.52f, y - 1 * s);
                y += rowH;
            }
        }
        Col("LINKER STICK", _r.Left, 4 * s);
        using (var pen = new Pen(Theme.Border, 1)) g.DrawLine(pen, colW - 4 * s, 2 * s, colW - 4 * s, Height - 4 * s);
        Col("RECHTER STICK", _r.Right, colW + 8 * s);
    }
}
