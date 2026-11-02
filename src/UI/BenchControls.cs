using System.Drawing.Drawing2D;
using Lagzero.Core;

namespace Lagzero.UI;

/// <summary>Horizontal tab bar.</summary>
internal sealed class TabStrip : LzControl
{
    private readonly List<(string Id, string Text)> _tabs = new();
    private readonly List<RectangleF> _rects = new();
    private int _hover = -1;
    public string Selected { get; private set; } = "";
    public event Action<string>? SelectedChanged;
    public Func<string, bool>? IsActive; // tab with running test gets a red dot

    public TabStrip() { BackColor = Theme.Bg; Cursor = Cursors.Hand; }

    public void Add(string id, string text) { _tabs.Add((id, text)); Invalidate(); }

    public void Select(string id)
    {
        if (Selected == id) return;
        Selected = id; Invalidate();
        SelectedChanged?.Invoke(id);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = _rects.FindIndex(r => r.Contains(e.Location));
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = _rects.FindIndex(r => r.Contains(e.Location));
        if (i >= 0) Select(_tabs[i].Id);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Bg);
        using var f = Theme.Display(9.5f, FontStyle.Bold);
        _rects.Clear();
        float x = 12 * s;
        foreach (var (id, text) in _tabs)
        {
            float w = Theme.MeasureTracked(g, text, f, 1.6f * s) + 26 * s;
            var r = new RectangleF(x, 0, w, Height);
            _rects.Add(r);
            bool sel = id == Selected, hov = _rects.Count - 1 == _hover;
            if (sel) using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, r);
            else if (hov) using (var b = new SolidBrush(Color.FromArgb(14, 14, 16))) g.FillRectangle(b, r);
            using (var tb = new SolidBrush(sel ? Theme.SilverLight : hov ? Theme.Silver : Theme.SilverDim))
                Theme.DrawTracked(g, text, f, tb, x + 13 * s, (Height - f.GetHeight(g)) / 2 + 1 * s, 1.6f * s);
            if (sel) using (var rb = new SolidBrush(Theme.Red)) g.FillRectangle(rb, r.X, Height - 3 * s, r.Width, 3 * s);
            if (IsActive?.Invoke(id) == true)
                using (var rb = new SolidBrush(Theme.RedHot)) g.FillEllipse(rb, r.Right - 10 * s, 7 * s, 5 * s, 5 * s);
            x += w + 2 * s;
        }
        using var pen = new Pen(Theme.Border, 1);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Grouped label / value / unit table with notes. Auto-sizes its height when AutoHeight is set.</summary>
internal sealed class KvView : LzControl
{
    public sealed record Row(string? Group, string Label, string Value, string Unit = "", bool Emphasis = false);

    private List<Row> _rows = new();
    private List<string> _notes = new();
    public string EmptyText { get; set; } = "Noch keine Messung.";
    public bool AutoHeight { get; set; }
    public float ValueColumn { get; set; } = 0.62f; // fraction of width where the value column ends (before unit)
    /// <summary>Text mode: values left-aligned in a second column (system info).</summary>
    public bool LeftValues { get; set; }

    public void SetData(IEnumerable<Row> rows, IEnumerable<string>? notes = null)
    {
        _rows = rows.ToList(); _notes = notes?.ToList() ?? new();
        UpdateHeight(); Invalidate();
    }

    public void SetResult(BenchResult? r)
    {
        if (r == null) { SetData(Array.Empty<Row>()); return; }
        var notes = new List<string>();
        if (r.Cancelled) notes.Add("⚠ Test abgebrochen – Werte unvollständig.");
        if (r.Error != null) notes.Add("⚠ Fehler: " + r.Error);
        notes.AddRange(r.Notes);
        notes.Add($"Gestartet {r.StartedAt:HH:mm:ss} · Dauer {r.DurationSec.ToString("0.0", Theme.De)} s");
        var rows = InfoLabels.Rows(r.Info).Select(x => new Row("Testbedingungen", x.Label, x.Value))
            .Concat(r.Metrics.Select(m => new Row(m.Group, m.Label, m.Value.ToString(m.Format, Theme.De), m.Unit,
                m.Unit.StartsWith("Punkte") || m.Key.EndsWith(".fps"))));
        SetData(rows, notes);
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateHeight(); }

    private float RowH => 22 * S;
    private float GroupH => 28 * S;

    private bool _sizing;

    private void UpdateHeight()
    {
        // re-entrancy guard: height change -> parent scrollbar toggles -> width change -> ...
        if (!AutoHeight || Width <= 0 || _sizing) return;
        _sizing = true;
        try
        {
            int h = (int)Math.Ceiling(Measure(null));
            if (Math.Abs(Height - h) > 1) Height = h;
        }
        finally { _sizing = false; }
    }

    /// <summary>Lays out (and optionally paints) everything; returns total height.</summary>
    private float Measure(Graphics? g)
    {
        float s = S, y = 2 * s;
        if (_rows.Count == 0 && _notes.Count == 0) return 40 * s;
        string? grp = null;
        foreach (var r in _rows)
        {
            if (r.Group != null && r.Group != grp) { grp = r.Group; y += GroupH; }
            y += RowH;
        }
        using var bmp = g == null ? new Bitmap(1, 1) : null;
        using var mg = g == null ? Graphics.FromImage(bmp!) : null;
        var gg = g ?? mg!;
        using var nf = Theme.Ui(8.5f);
        y += 6 * s;
        foreach (var n in _notes) y += gg.MeasureString(n, nf, Math.Max(50, Width - 8)).Height + 3 * s;
        return y + 6 * s;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(BackColor);
        using var dim = new SolidBrush(Theme.SilverDim);
        using var silver = new SolidBrush(Theme.Silver);
        using var light = new SolidBrush(Theme.SilverLight);
        using var red = new SolidBrush(Theme.Red);
        using var lab = Theme.Ui(9.5f);
        using var val = Theme.Mono(10f);
        using var valB = Theme.Mono(10f, FontStyle.Bold);
        using var grpF = Theme.Display(8.5f, FontStyle.Bold);
        using var nf = Theme.Ui(8.5f);
        if (_rows.Count == 0 && _notes.Count == 0)
        {
            g.DrawString(EmptyText, lab, dim, 4 * s, 8 * s);
            return;
        }
        float y = 2 * s, unitW = 120 * s;
        float valRight = Math.Max(Width * ValueColumn, 200 * s);
        if (valRight + unitW > Width) valRight = Width - unitW;
        string? grp = null; int i = 0;
        using var zebra = new SolidBrush(Color.FromArgb(9, 255, 255, 255));
        using var rf = new StringFormat { Alignment = StringAlignment.Far };
        foreach (var r in _rows)
        {
            if (r.Group != null && r.Group != grp)
            {
                grp = r.Group;
                Theme.DrawTracked(g, grp.ToUpperInvariant(), grpF, red, 4 * s, y + 10 * s, 1.5f * s);
                y += GroupH; i = 0;
            }
            if (i++ % 2 == 0) g.FillRectangle(zebra, 0, y, Width, RowH);
            if (LeftValues)
            {
                float vx = Math.Max(150 * s, Width * 0.26f);
                var nw = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(r.Label, lab, dim, new RectangleF(4 * s, y + 2 * s, vx - 10 * s, RowH), nw);
                g.DrawString(r.Value, lab, r.Emphasis ? light : silver, new RectangleF(vx, y + 2 * s, Width - vx - 4 * s, RowH), nw);
                y += RowH;
                continue;
            }
            g.DrawString(r.Label, lab, silver, new RectangleF(4 * s, y + 2 * s, valRight - 140 * s, RowH), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            g.DrawString(r.Value, r.Emphasis ? valB : val, r.Emphasis ? light : silver, valRight, y + 2 * s, rf);
            g.DrawString(r.Unit, nf, dim, valRight + 6 * s, y + 4 * s);
            y += RowH;
        }
        y += 6 * s;
        using var warn = new SolidBrush(Theme.RedHot);
        foreach (var n in _notes)
        {
            var sz = g.MeasureString(n, nf, Math.Max(50, Width - 8));
            g.DrawString(n, nf, n.StartsWith("⚠") ? warn : dim, new RectangleF(4 * s, y, Width - 8, sz.Height + 2));
            y += sz.Height + 3 * s;
        }
    }
}

/// <summary>Line chart for a series (frame times, throughput over time ...).</summary>
internal sealed class SeriesChart : LzControl
{
    private double[]? _v;
    private string _label = "";
    public string Unit { get; set; } = "ms";
    public double? Marker { get; set; } // horizontal marker (e.g. stutter threshold)

    public void SetSeries(double[]? v, string label) { _v = v; _label = label; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(Theme.Panel);
        using var small = Theme.Mono(7.5f);
        using var dim = new SolidBrush(Theme.SilverDim);
        float left = 52 * s, right = Width - 6 * s, top = 6 * s, bottom = Height - 18 * s;
        using (var ax = new Pen(Theme.Border, 1)) { g.DrawLine(ax, left, top, left, bottom); g.DrawLine(ax, left, bottom, right, bottom); }
        if (_v is not { Length: > 1 } v) { g.DrawString("Noch keine Daten", small, dim, left + 8 * s, top + 6 * s); return; }
        var sorted = v.OrderBy(x => x).ToArray();
        double med = sorted[sorted.Length / 2];
        double yMax = Math.Max(sorted[(int)(sorted.Length * 0.999)] * 1.15, med * 2.2);
        if (Marker is { } mk) yMax = Math.Max(yMax, mk * 1.15);
        float W = right - left, H = bottom - top;
        float X(int i) => left + (float)i / (v.Length - 1) * W;
        float Y(double val) => bottom - (float)(Math.Min(val, yMax) / yMax) * H;
        using (var grid = new Pen(Color.FromArgb(30, 255, 255, 255), 1) { DashStyle = DashStyle.Dot })
            for (int k = 1; k <= 3; k++)
            {
                double gv = yMax * k / 4; float yy = Y(gv);
                g.DrawLine(grid, left, yy, right, yy);
                g.DrawString(gv.ToString("0.0", Theme.De), small, dim, 2 * s, yy - 6 * s);
            }
        g.DrawString(Unit, small, dim, 2 * s, top);
        // decimate to pixel columns (min/max per column)
        int cols = Math.Max(2, (int)W);
        using var pen = new Pen(Theme.Silver, 1.1f * s);
        using var range = new Pen(Color.FromArgb(70, Theme.Silver), 1f);
        using var spike = new Pen(Theme.RedHot, 1.3f * s);
        PointF? prev = null;
        for (int c = 0; c < cols; c++)
        {
            int a = (int)((long)c * v.Length / cols), b = Math.Max(a + 1, (int)((long)(c + 1) * v.Length / cols));
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = a; i < b && i < v.Length; i++) { lo = Math.Min(lo, v[i]); hi = Math.Max(hi, v[i]); }
            // translucent min..max band per pixel column, median-ish line on top, spikes above the marker in red
            float x = left + c * W / (cols - 1);
            if (hi - lo > 0) g.DrawLine(Marker is { } m && hi > m ? spike : range, x, Y(lo), x, Y(hi));
            var pt = new PointF(x, Y(lo + (hi - lo) * 0.25));
            if (prev is { } p) g.DrawLine(pen, p, pt);
            prev = pt;
        }
        float my = Y(med);
        using (var mp = new Pen(Theme.Red, 1.2f * s) { DashStyle = DashStyle.Dash }) g.DrawLine(mp, left, my, right, my);
        if (Marker is { } mm)
            using (var mp = new Pen(Color.FromArgb(150, Theme.RedHot), 1f) { DashStyle = DashStyle.Dot }) g.DrawLine(mp, left, Y(mm), right, Y(mm));
        g.DrawString($"{_label} · Median {med.ToString("0.00", Theme.De)} {Unit} (rot gestrichelt)", small, dim, left, bottom + 3 * s);
    }
}

/// <summary>Standard page for one benchmark: description, start/cancel, progress, result table (+ optional extras / chart).</summary>
internal class TestPage : UserControl
{
    public string TestId { get; }
    public Func<IBenchmark?> Factory { get; set; } = () => null;
    public event Action<TestPage>? StartRequested;
    public event Action? CancelRequested;

    protected readonly LzButton StartButton = new("TEST STARTEN", primary: true);
    protected readonly ProgressView Progress = new();
    protected readonly KvView Table = new() { AutoHeight = true, Dock = DockStyle.Top };
    protected readonly Card ResultCard;
    public SeriesChart? Chart { get; }
    private bool _running;

    /// <summary>Not part of "Alles testen" (e.g. the internet test – own click only).</summary>
    public bool ExcludeFromAll { get; init; }

    /// <summary>Status line of the main window (set by MainForm).</summary>
    public Action<string, bool>? Status { get; set; }

    public TestPage(string testId, string title, string description, Control? extras = null, int extrasHeight = 0, bool chart = false, Card? sideCard = null)
    {
        TestId = testId;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(10, 8, 10, 8);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        if (extras != null) root.RowStyles.Add(new RowStyle(SizeType.Absolute, extrasHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, chart ? 58 : 100));
        if (chart) root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowCount = root.RowStyles.Count;

        var head = new Card(title) { Dock = DockStyle.Fill };
        var ht = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Panel, Margin = Padding.Empty };
        ht.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ht.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        ht.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ht.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var desc = new Label
        {
            Text = description, Dock = DockStyle.Fill, ForeColor = Theme.Silver, BackColor = Theme.Panel,
            Font = Theme.Ui(9f), Padding = new Padding(0, 0, 12, 0),
        };
        StartButton.Dock = DockStyle.Fill; StartButton.Margin = new Padding(0, 2, 0, 6);
        StartButton.Click += (_, _) => { if (_running) CancelRequested?.Invoke(); else StartRequested?.Invoke(this); };
        Progress.Dock = DockStyle.Fill; Progress.Margin = new Padding(0, 6, 0, 0);
        ht.Controls.Add(desc, 0, 0);
        ht.Controls.Add(StartButton, 1, 0);
        ht.Controls.Add(Progress, 0, 1); ht.SetColumnSpan(Progress, 2);
        head.Controls.Add(ht);
        root.Controls.Add(head);

        if (extras != null)
        {
            var ec = new Card("EINSTELLUNGEN") { Dock = DockStyle.Fill };
            extras.Dock = DockStyle.Fill;
            ec.Controls.Add(extras);
            root.Controls.Add(ec);
        }

        ResultCard = new Card("ERGEBNIS") { Dock = DockStyle.Fill, AutoScroll = true };
        Table.BackColor = Theme.Panel;
        ResultCard.Controls.Add(Table);
        root.Controls.Add(ResultCard);

        if (chart)
        {
            var cc = new Card("VERLAUF") { Dock = DockStyle.Fill };
            Chart = new SeriesChart { Dock = DockStyle.Fill };
            cc.Controls.Add(Chart);
            if (sideCard == null) root.Controls.Add(cc);
            else
            {
                var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Bg };
                split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
                split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
                sideCard.Dock = DockStyle.Fill;
                split.Controls.Add(cc, 0, 0); split.Controls.Add(sideCard, 1, 0);
                root.Controls.Add(split);
            }
        }
        Controls.Add(root);
    }

    public void SetBusy(bool runningHere, bool anyBusy)
    {
        _running = runningHere;
        StartButton.Text = runningHere ? "ABBRECHEN" : "TEST STARTEN";
        StartButton.Enabled = runningHere || !anyBusy;
        OnBusyChanged(anyBusy);
    }

    protected virtual void OnBusyChanged(bool anyBusy) { }

    public void ReportProgress(BenchProgress p)
    {
        Progress.Value = p.Fraction;
        Progress.Status = p.Text.ToUpperInvariant();
        Progress.Invalidate();
    }

    public virtual void ShowResult(BenchResult? r)
    {
        Table.SetResult(r);
        Chart?.SetSeries(r?.Series, r?.SeriesLabel ?? "");
    }
}
