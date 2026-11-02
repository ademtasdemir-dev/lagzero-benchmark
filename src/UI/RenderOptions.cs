using Lagzero.Core;

namespace Lagzero.UI;

/// <summary>Resolution picker (+ optional VSync) for the render tests. Resolutions the primary monitor does not support are greyed out.</summary>
internal sealed class RenderOptions : TableLayoutPanel
{
    private sealed record Option(string Text, int W, int H, bool IsMonitor, bool Supported)
    {
        public override string ToString() => Supported ? Text : Text + "  – vom Monitor nicht unterstützt";
    }

    private readonly ComboBox _combo = Ui.Combo();
    private readonly CheckBox? _vsync;
    private int _lastGood;
    public Action<string, bool>? Status;

    public RenderOptions(bool withVsync)
    {
        BackColor = Theme.Panel; Margin = Padding.Empty;
        ColumnCount = 3; RowCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _combo.DrawMode = DrawMode.OwnerDrawFixed;
        _combo.ItemHeight = 20;
        _combo.DrawItem += DrawItem;
        _combo.SelectedIndexChanged += (_, _) =>
        {
            if (_combo.SelectedItem is Option { Supported: false })
            {
                _combo.SelectedIndex = _lastGood;
                Status?.Invoke("Diese Auflösung unterstützt der Hauptmonitor nicht – Auswahl nicht möglich.", true);
            }
            else _lastGood = _combo.SelectedIndex;
        };
        Controls.Add(Ui.Caption("AUFLÖSUNG"), 0, 0);
        Controls.Add(_combo, 1, 0);
        if (withVsync)
        {
            _vsync = new CheckBox
            {
                Text = "VSync an (an Bildwiederholrate gekoppelt)", ForeColor = Theme.Silver, BackColor = Theme.Panel,
                FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, Font = Theme.Ui(9f), Margin = new Padding(14, 4, 3, 3),
            };
            _vsync.FlatAppearance.BorderColor = Theme.SilverDim;
            _vsync.FlatAppearance.CheckedBackColor = Theme.Red;
            Controls.Add(_vsync, 2, 0);
        }
        var hint = Ui.Text("Standard: native Auflösung und Bildwiederholrate des Hauptmonitors (physische Pixel, unabhängig von der Windows-Skalierung). " +
                           "Gerendert wird immer in der gewählten Auflösung; das Testfenster zeigt das Bild verkleinert an.");
        hint.ForeColor = Theme.SilverDim; hint.Font = Theme.Ui(8.5f);
        Controls.Add(hint, 0, 1); SetColumnSpan(hint, 3);
        Reload();
    }

    public void Reload()
    {
        var m = Monitors.Primary();
        _combo.Items.Clear();
        if (m != null) _combo.Items.Add(new Option($"Monitor-Auflösung ({m.Width}×{m.Height} @ {m.RefreshHz} Hz)", m.Width, m.Height, true, true));
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3840, 2160) })
            _combo.Items.Add(new Option($"{w}×{h}", w, h, false, m == null || m.Supports(w, h)));
        _combo.SelectedIndex = _lastGood = 0;
    }

    /// <summary>For scripted checks (--res 1920x1080). Returns false if not available.</summary>
    public bool Select(int w, int h)
    {
        for (int i = 0; i < _combo.Items.Count; i++)
            if (_combo.Items[i] is Option { IsMonitor: false, Supported: true } o && o.W == w && o.H == h) { _combo.SelectedIndex = i; return true; }
        return false;
    }

    public RenderSettings Current()
    {
        var o = (Option)_combo.SelectedItem!;
        return new RenderSettings(o.W, o.H, _vsync?.Checked == true, o.IsMonitor);
    }

    public void SetEnabled(bool on) { _combo.Enabled = on; if (_vsync != null) _vsync.Enabled = on; }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var o = (Option)_combo.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0 && o.Supported;
        using (var bg = new SolidBrush(sel ? Theme.RedDark : Theme.PanelHi)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, o.ToString(), _combo.Font, e.Bounds,
            o.Supported ? Theme.Silver : Theme.SilverDark, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
