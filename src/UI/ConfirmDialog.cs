namespace Lagzero.UI;

/// <summary>
/// Safety confirmation for write tests. Only an explicit click on the confirm button returns true;
/// closing the window, Esc, Alt+F4, WM_CLOSE or owner shutdown always mean "no".
/// </summary>
internal sealed class ConfirmDialog : Form
{
    private bool _confirmed;

    private ConfirmDialog(string title, string message, string confirmText, bool danger, string? heading = null)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel;
        ForeColor = Theme.Silver;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(660, 360);
        KeyPreview = true;

        var head = new Label
        {
            Dock = DockStyle.Top, Height = 40, Text = heading ?? (danger ? "⚠  SCHREIBTEST AUF DEM SYSTEMLAUFWERK" : "SCHREIBTEST BESTÄTIGEN"),
            ForeColor = danger ? Theme.RedHot : Theme.SilverLight, BackColor = Theme.Bg, Font = Theme.Display(11, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0),
        };
        var rule = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Theme.Red };
        var body = new Label
        {
            Dock = DockStyle.Fill, Text = message, ForeColor = Theme.Silver, BackColor = Theme.Panel,
            Font = Theme.Ui(9.5f), Padding = new Padding(16, 12, 16, 8),
        };
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 64, ColumnCount = 3, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 12) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        var no = new LzButton("NEIN, ABBRECHEN") { Dock = DockStyle.Fill };
        var yes = new LzButton(confirmText, primary: true) { Dock = DockStyle.Fill };
        no.Click += (_, _) => { _confirmed = false; Close(); };
        yes.Click += (_, _) => { _confirmed = true; Close(); };
        bar.Controls.Add(new Panel { BackColor = Theme.Panel }, 0, 0);
        bar.Controls.Add(no, 1, 0);
        bar.Controls.Add(yes, 2, 0);
        Controls.Add(body); Controls.Add(bar); Controls.Add(rule); Controls.Add(head);
        Shown += (_, _) => no.Focus(); // safe default
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { _confirmed = false; Close(); }
        base.OnKeyDown(e);
    }

    /// <summary>True only after an explicit click on the confirm button.</summary>
    public static bool Ask(IWin32Window? owner, string title, string message, string confirmText, bool danger, string? heading = null)
    {
        using var dlg = new ConfirmDialog(title, message, confirmText, danger, heading);
        dlg.ShowDialog(owner);
        return dlg._confirmed;
    }
}
