using System.Diagnostics;
using Lagzero.Core;

namespace Lagzero.UI;

internal static class Ui
{
    public static ComboBox Combo(int dropWidth = 0)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelHi, ForeColor = Theme.Silver, Font = Theme.Ui(9.5f),
            Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3),
        };
        if (dropWidth > 0) cb.DropDownWidth = dropWidth;
        return cb;
    }

    public static Label Caption(string text) => new()
    {
        Text = text, ForeColor = Theme.SilverDim, Font = Theme.Display(8.5f, FontStyle.Bold),
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel,
    };

    public static Label Text(string text) => new()
    {
        Text = text, ForeColor = Theme.Silver, Font = Theme.Ui(9f), Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel,
    };

    public static void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    public static IEnumerable<KvView.Row> InfoRows(IEnumerable<InfoItem> items) =>
        items.Select(i => new KvView.Row(i.Section, i.Label, i.Value));
}

/// <summary>System information page (head of every report).</summary>
internal sealed class SystemPage : UserControl
{
    private readonly KvView _kv = new() { AutoHeight = true, Dock = DockStyle.Top, LeftValues = true, EmptyText = "System-Informationen werden gelesen …" };
    public event Action? RefreshRequested;

    public SystemPage()
    {
        BackColor = Theme.Bg; Dock = DockStyle.Fill; Padding = new Padding(10, 8, 10, 8);
        var card = new Card("SYSTEM-INFORMATION") { Dock = DockStyle.Fill, AutoScroll = true };
        _kv.BackColor = Theme.Panel;
        card.Controls.Add(_kv);
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg, Padding = new Padding(6, 4, 6, 6) };
        var btn = new LzButton("NEU EINLESEN") { Dock = DockStyle.Right, Width = 160 };
        btn.Click += (_, _) => RefreshRequested?.Invoke();
        var note = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Theme.SilverDim, BackColor = Theme.Bg, Font = Theme.Ui(8.5f), TextAlign = ContentAlignment.MiddleLeft,
            Text = "Quelle: WMI und Registry · wird als Kopf in jeden Bericht und jeden Verlaufseintrag übernommen.",
        };
        bar.Controls.Add(note); bar.Controls.Add(btn);
        Controls.Add(card); Controls.Add(bar);
    }

    public void ShowInfo(List<InfoItem> items) => _kv.SetData(Ui.InfoRows(items));
}

/// <summary>All results of the current run on one page + export.</summary>
internal sealed class OverviewPage : UserControl
{
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    private readonly List<Card> _cards = new();
    private readonly Label _title = new();
    public event Action? ExportJson, ExportHtml, NewRun;
    private readonly CheckBox _devices = new();
    /// <summary>Include the USB device list in HTML/JSON export (off by default – privacy).</summary>
    public bool IncludeDevices => _devices.Checked;

    public OverviewPage()
    {
        BackColor = Theme.Bg; Dock = DockStyle.Fill; Padding = new Padding(10, 8, 10, 8);
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 5, BackColor = Theme.Bg, Padding = new Padding(6, 2, 6, 6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        for (int i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        _title.Dock = DockStyle.Fill; _title.ForeColor = Theme.SilverDim; _title.BackColor = Theme.Bg; _title.Font = Theme.Ui(9f);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        var bJson = new LzButton("JSON EXPORT") { Dock = DockStyle.Fill };
        var bHtml = new LzButton("HTML-BERICHT") { Dock = DockStyle.Fill };
        var bNew = new LzButton("NEUER LAUF") { Dock = DockStyle.Fill };
        bJson.Click += (_, _) => ExportJson?.Invoke();
        bHtml.Click += (_, _) => ExportHtml?.Invoke();
        bNew.Click += (_, _) => NewRun?.Invoke();
        _devices.Text = "Geräteliste im Bericht"; _devices.ForeColor = Theme.SilverDim; _devices.BackColor = Theme.Bg;
        _devices.FlatStyle = FlatStyle.Flat; _devices.Dock = DockStyle.Fill; _devices.Font = Theme.Ui(8.5f);
        _devices.FlatAppearance.BorderColor = Theme.SilverDim; _devices.FlatAppearance.CheckedBackColor = Theme.Red;
        bar.Controls.Add(_title, 0, 0); bar.Controls.Add(_devices, 1, 0); bar.Controls.Add(bNew, 2, 0); bar.Controls.Add(bJson, 3, 0); bar.Controls.Add(bHtml, 4, 0);
        _scroll.Resize += (_, _) => DoLayoutCards();
        Controls.Add(_scroll); Controls.Add(bar);
    }

    private sealed record Entry(Card Card, Control Content, bool Full, bool Detail);
    private readonly List<Entry> _entries = new();
    private bool _details;
    private Session? _last;

    public void ShowSession(Session s)
    {
        _last = s;
        _title.Text = $"Lauf {s.StartedAt:dd.MM.yyyy HH:mm:ss} · {s.Results.Count} Test(s) · gespeichert im Verlauf · nur gemessene Werte";
        _scroll.SuspendLayout();
        _layouting = true;                      // no re-layout while old cards are torn down
        var old = _entries.ToList();
        _entries.Clear(); _cards.Clear();
        foreach (var e in old) { _scroll.Controls.Remove(e.Card); e.Card.Dispose(); }
        _layouting = false;

        // 1) plain-language evaluation
        Evaluation ev;
        try { ev = Evaluator.Evaluate(s, HistoryStore.LoadAll().Select(x => x.Session)); }
        catch (Exception ex) { ev = new Evaluation { Fazit = { "Auswertung nicht möglich: " + ex.Message } }; }
        var fz = new EvalView(); fz.SetFazit(ev);
        Add("FAZIT", fz, full: true, detail: false);
        foreach (var sec in ev.Sections)
        {
            var v = new EvalView(); v.SetSection(sec);
            Add(sec.Title.ToUpperInvariant(), v, full: false, detail: false);
        }

        // 2) raw values, collapsible
        var toggle = new LzButton(_details ? "DETAILS AUSBLENDEN ▴" : "DETAILS · ROHWERTE ANZEIGEN ▾") { Height = 36, Dock = DockStyle.Top };
        toggle.Click += (_, _) => { _details = !_details; if (_last != null) ShowSession(_last); };
        Add("DETAILS · ROHWERTE", toggle, full: true, detail: false);

        var sysRows = Ui.InfoRows(s.SystemInfo).ToList();
        Add("SYSTEM", Kv(kv => { kv.LeftValues = true; kv.EmptyText = "System-Informationen werden gelesen …"; kv.SetData(sysRows); }), full: true, detail: true);
        if (s.Results.Count == 0)
            Add("ERGEBNISSE", Kv(kv => { kv.EmptyText = "Noch keine Tests in diesem Lauf. Einzelne Tests in den Reitern starten oder ALLES TESTEN."; kv.SetData(Array.Empty<KvView.Row>()); }), false, true);
        foreach (var r in s.Results.Values.OrderBy(r => HtmlReport.Order(r.TestId)))
            Add(r.Title.ToUpperInvariant(), Kv(kv => kv.SetResult(r)), full: false, detail: true);
        _scroll.ResumeLayout();
        DoLayoutCards();
    }

    private static KvView Kv(Action<KvView> fill)
    {
        var kv = new KvView { AutoHeight = true, Dock = DockStyle.Top, BackColor = Theme.Panel, ValueColumn = 0.7f };
        fill(kv);
        return kv;
    }

    private void Add(string title, Control content, bool full, bool detail)
    {
        var card = new Card(title);
        content.Dock = DockStyle.Top;
        card.Controls.Add(content);
        content.SizeChanged += (_, _) => DoLayoutCards();
        card.Visible = !detail || _details;
        _entries.Add(new Entry(card, content, full, detail));
        _cards.Add(card);
        _scroll.Controls.Add(card);
    }

    private bool _layouting;

    private void DoLayoutCards()
    {
        if (_layouting || _entries.Count == 0) return;
        _layouting = true;
        try
        {
            float s = DeviceDpi / 96f;
            int gap = (int)(10 * s);
            int avail = _scroll.ClientSize.Width - gap;
            int cols = avail > 1100 * s ? 2 : 1;
            int w = (avail - gap * cols) / cols;
            var colY = new int[cols];
            int scrollY = _scroll.AutoScrollPosition.Y;
            foreach (var e in _entries)
            {
                if (e.Card.IsDisposed || !e.Card.Visible) continue;
                bool full = e.Full && cols == 2;
                e.Card.Width = full ? avail - gap : w;          // lets the content compute its height for this width
                int h = e.Content.Height + e.Card.Padding.Vertical + 2;
                int top = full ? colY.Max() : colY.Min();
                int col = full ? 0 : Array.IndexOf(colY, colY.Min());
                e.Card.SetBounds(gap / 2 + col * (w + gap), top + scrollY, e.Card.Width, h);
                if (full) for (int k = 0; k < cols; k++) colY[k] = top + h + gap;
                else colY[col] += h + gap;
            }
        }
        finally { _layouting = false; }
    }
}
/// <summary>Run history: pick two runs and compare (before / after) with difference in percent.</summary>
internal sealed class HistoryPage : UserControl
{
    private readonly ComboBox _a = Ui.Combo(520), _b = Ui.Combo(520);
    private readonly CompareView _view = new() { Dock = DockStyle.Top };
    private List<(string Path, Session Session)> _runs = new();
    private string? _lastNewest;

    public HistoryPage()
    {
        BackColor = Theme.Bg; Dock = DockStyle.Fill; Padding = new Padding(10, 8, 10, 8);
        var head = new Card("VERLAUF · VORHER / NACHHER") { Dock = DockStyle.Top, Height = 138 };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, BackColor = Theme.Panel };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var reload = new LzButton("AKTUALISIEREN") { Dock = DockStyle.Fill, Margin = new Padding(3, 3, 0, 3) };
        var folder = new LzButton("ORDNER ÖFFNEN") { Dock = DockStyle.Fill, Margin = new Padding(3, 3, 0, 3) };
        reload.Click += (_, _) => Reload();
        folder.Click += (_, _) => Ui.OpenFolder(HistoryStore.Dir);
        t.Controls.Add(Ui.Caption("LAUF A · VORHER"), 0, 0); t.Controls.Add(_a, 1, 0); t.Controls.Add(reload, 2, 0);
        t.Controls.Add(Ui.Caption("LAUF B · NACHHER"), 0, 1); t.Controls.Add(_b, 1, 1); t.Controls.Add(folder, 2, 1);
        var hint = Ui.Text("Jeder Lauf wird automatisch gespeichert. Differenz = (B − A) / A. ▲ besser · ▼ schlechter (je nach Messwert höher oder niedriger besser).");
        hint.ForeColor = Theme.SilverDim; hint.Font = Theme.Ui(8.5f);
        t.Controls.Add(hint, 0, 2); t.SetColumnSpan(hint, 3);
        head.Controls.Add(t);

        var body = new Card("VERGLEICH") { Dock = DockStyle.Fill, AutoScroll = true };
        _view.BackColor = Theme.Panel;
        body.Controls.Add(_view);
        var gap = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg };
        Controls.Add(body); Controls.Add(gap); Controls.Add(head);

        _a.SelectedIndexChanged += (_, _) => Compare();
        _b.SelectedIndexChanged += (_, _) => Compare();
    }

    private sealed record RunItem(Session S)
    {
        public override string ToString() => S.Summary;
    }

    public void Reload()
    {
        _runs = HistoryStore.LoadAll();
        string? selA = (_a.SelectedItem as RunItem)?.S.Id, selB = (_b.SelectedItem as RunItem)?.S.Id;
        string? newest = _runs.FirstOrDefault().Session?.Id;
        if (newest != _lastNewest) { selA = selB = null; _lastNewest = newest; }   // a new run arrived: default to newest vs. previous
        foreach (var cb in new[] { _a, _b })
        {
            cb.BeginUpdate(); cb.Items.Clear();
            foreach (var r in _runs) cb.Items.Add(new RunItem(r.Session));
            cb.EndUpdate();
        }
        int Find(string? id) => _runs.FindIndex(r => r.Session.Id == id);
        // default: B = newest, A = the one before
        _b.SelectedIndex = Find(selB) is >= 0 and var ib ? ib : (_runs.Count > 0 ? 0 : -1);
        _a.SelectedIndex = Find(selA) is >= 0 and var ia ? ia : (_runs.Count > 1 ? 1 : _runs.Count > 0 ? 0 : -1);
        Compare();
    }

    private void Compare()
    {
        if (_a.SelectedItem is RunItem a && _b.SelectedItem is RunItem b)
            _view.SetData(a.S, b.S, HistoryStore.Compare(a.S, b.S));
        else _view.SetData(null, null, new());
    }
}

/// <summary>Comparison table A | B | Δ %.</summary>
internal sealed class CompareView : LzControl
{
    private List<HistoryStore.CompareRow> _rows = new();
    private Session? _a, _b;

    public void SetData(Session? a, Session? b, List<HistoryStore.CompareRow> rows)
    {
        _a = a; _b = b; _rows = rows;
        float s = S;
        int groups = rows.Select(r => r.Test).Distinct().Count();
        int warnings = rows.Where(r => r.Blocker != null).Select(r => r.Test).Distinct().Count();
        Height = (int)(40 * s + rows.Count * 22 * s + groups * 28 * s + warnings * 26 * s + 20 * s);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Hq(g);
        float s = S;
        g.Clear(BackColor);
        using var lab = Theme.Ui(9.5f);
        using var val = Theme.Mono(9.5f);
        using var head = Theme.Display(8.5f, FontStyle.Bold);
        using var dim = new SolidBrush(Theme.SilverDim);
        using var silver = new SolidBrush(Theme.Silver);
        using var light = new SolidBrush(Theme.SilverLight);
        using var red = new SolidBrush(Theme.Red);
        using var hot = new SolidBrush(Theme.RedHot);
        if (_a == null || _b == null)
        {
            g.DrawString("Noch keine gespeicherten Läufe. Nach jedem Test wird der aktuelle Lauf automatisch im Verlauf abgelegt.", lab, dim, 4 * s, 8 * s);
            return;
        }
        float cA = Width * 0.58f, cB = Width * 0.74f, cD = Width - 8 * s;
        using var rf = new StringFormat { Alignment = StringAlignment.Far };
        float y = 4 * s;
        g.DrawString($"A: {_a.StartedAt:dd.MM.yy HH:mm}", head, dim, cA, y, rf);
        g.DrawString($"B: {_b.StartedAt:dd.MM.yy HH:mm}", head, dim, cB, y, rf);
        g.DrawString("Δ %", head, red, cD, y, rf);
        y += 26 * s;
        string? grp = null; int i = 0;
        using var zebra = new SolidBrush(Color.FromArgb(9, 255, 255, 255));
        var nw = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        foreach (var r in _rows)
        {
            if (r.Test != grp)
            {
                grp = r.Test;
                Theme.DrawTracked(g, grp.ToUpperInvariant(), head, red, 4 * s, y + 10 * s, 1.5f * s);
                y += 28 * s; i = 0;
                if (_rows.FirstOrDefault(x => x.Test == grp && x.Blocker != null)?.Blocker is { } warn)
                {
                    using var wb = new SolidBrush(Color.FromArgb(60, Theme.Red));
                    g.FillRectangle(wb, 0, y, Width, 22 * s);
                    g.FillRectangle(hot, 0, y, 3 * s, 22 * s);
                    g.DrawString($"⚠ {warn} – 3D-/Frametime-Werte werden nicht in Prozent verglichen (andere Auflösung bzw. VSync).", lab, light, 10 * s, y + 2 * s);
                    y += 26 * s;
                }
            }
            if (i++ % 2 == 0) g.FillRectangle(zebra, 0, y, Width, 22 * s);
            g.DrawString(r.Label + (r.Unit.Length > 0 ? $"  [{r.Unit}]" : ""), lab, silver, new RectangleF(4 * s, y + 2 * s, cA - 140 * s, 22 * s), nw);
            g.DrawString(r.A?.ToString(r.Format, Theme.De) ?? "—", val, silver, cA, y + 2 * s, rf);
            g.DrawString(r.B?.ToString(r.Format, Theme.De) ?? "—", val, light, cB, y + 2 * s, rf);
            if (r.DiffPct is { } d)
            {
                string arrow = r.Trend > 0 ? "▲ " : r.Trend < 0 ? "▼ " : "";
                g.DrawString(arrow + d.ToString("+0.0;-0.0;0.0", Theme.De) + " %", val, r.Trend < 0 ? hot : r.Trend > 0 ? light : dim, cD, y + 2 * s, rf);
            }
            else if (r.Blocker != null) g.DrawString("nicht vergleichbar", lab, hot, cD, y + 2 * s, rf);
            else g.DrawString("—", val, dim, cD, y + 2 * s, rf);
            y += 22 * s;
        }
    }
}
