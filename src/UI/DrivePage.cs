using Lagzero.Core;

namespace Lagzero.UI;

/// <summary>Drive / USB page: drive selection, safety checks and confirmation before every write test.</summary>
internal sealed class DrivePage : TestPage
{
    private readonly ComboBox _drive;
    private readonly ComboBox _size;
    private readonly CheckBox _allowSystem;
    private readonly LzButton _scan;
    private readonly KvView _usb;

    private sealed record SizeOption(long Mb)
    {
        public override string ToString() => Mb >= 1024 ? $"{Mb / 1024} GB Testdatei" : $"{Mb} MB Testdatei";
    }

    private sealed record NoDrive
    {
        public override string ToString() => "— Laufwerk auswählen —";
    }

    private DrivePage(Control extras, ComboBox drive, ComboBox size, CheckBox allow, LzButton scan, Card usbCard, KvView usb)
        : base("drive", "LAUFWERKE & USB",
            "Sequenziell 1 MiB (QD8) lesen/schreiben und 4K zufällig mit Queue Depth 1 und 32 – mit ungepuffertem I/O. " +
            "Die Testdatei liegt nur im eigenen Ordner LAGZERO_Benchmark_Temp auf dem gewählten Laufwerk und wird danach gelöscht; " +
            "bestehende Dateien werden nie angefasst. Vor jedem Schreibtest erscheint eine Bestätigung.",
            extras, 150, chart: true, sideCard: usbCard)
    {
        _drive = drive; _size = size; _allowSystem = allow; _scan = scan; _usb = usb;
        _scan.Click += (_, _) => Reload();
        _drive.SelectedIndexChanged += (_, _) => UpdateHint();
        _allowSystem.CheckedChanged += (_, _) => UpdateHint();
        Factory = CreateBenchmark;
        if (Chart != null) Chart.Unit = "MB/s";
        Reload();
    }

    private Label? _hint;

    public static DrivePage Build()
    {
        var drive = Ui.Combo(700);
        var size = Ui.Combo();
        foreach (var mb in new long[] { 256, 1024, 4096 }) size.Items.Add(new SizeOption(mb));
        size.SelectedIndex = 1;
        var allow = new CheckBox
        {
            Text = "Systemlaufwerk ausdrücklich zulassen", ForeColor = Theme.Silver, BackColor = Theme.Panel,
            FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, Font = Theme.Ui(9f), Margin = new Padding(12, 4, 3, 3),
        };
        allow.FlatAppearance.BorderColor = Theme.SilverDim;
        allow.FlatAppearance.CheckedBackColor = Theme.Red;
        var scan = new LzButton("SCAN") { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 0, 3) };

        var t = new TableLayoutPanel { ColumnCount = 4, RowCount = 3, BackColor = Theme.Panel, Margin = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(Ui.Caption("LAUFWERK"), 0, 0);
        t.Controls.Add(drive, 1, 0); t.SetColumnSpan(drive, 2);
        t.Controls.Add(scan, 3, 0);
        t.Controls.Add(Ui.Caption("TESTGRÖSSE"), 0, 1);
        t.Controls.Add(size, 1, 1);
        t.Controls.Add(allow, 2, 1); t.SetColumnSpan(allow, 2);
        var hint = Ui.Text("");
        hint.ForeColor = Theme.SilverDim; hint.Font = Theme.Ui(8.5f);
        t.Controls.Add(hint, 0, 2); t.SetColumnSpan(hint, 4);

        var usbCard = new Card("USB-GERÄTE") { AutoScroll = true };
        var usb = new KvView { AutoHeight = true, Dock = DockStyle.Top, LeftValues = true, BackColor = Theme.Panel, EmptyText = "USB-Geräte werden gelesen …" };
        usbCard.Controls.Add(usb);

        var page = new DrivePage(t, drive, size, allow, scan, usbCard, usb) { _hint = hint };
        page.UpdateHint();
        return page;
    }

    private Task _loading = Task.CompletedTask;

    public void Reload() => _loading = ReloadCore();

    /// <summary>Selects a drive by letter once the list is loaded (scripted checks: --drive X).</summary>
    public async Task SelectAsync(string letter)
    {
        await _loading;
        for (int i = 0; i < _drive.Items.Count; i++)
            if (_drive.Items[i] is DriveTarget d && d.Letter.Equals(letter.TrimEnd(':', '\\'), StringComparison.OrdinalIgnoreCase)) _drive.SelectedIndex = i;
    }

    private async Task ReloadCore()
    {
        var prev = (_drive.SelectedItem as DriveTarget)?.Root;
        _drive.Items.Clear();
        _drive.Items.Add(new NoDrive());
        _drive.SelectedIndex = 0;
        var (drives, usb) = await Task.Run(() => (DriveCatalog.Enumerate(), SystemInfo.UsbDevices()));
        foreach (var d in drives) _drive.Items.Add(d);
        if (prev != null && drives.FindIndex(d => d.Root == prev) is >= 0 and var i) _drive.SelectedIndex = i + 1;
        _usb.SetData(usb.Select(u => new KvView.Row(null, u.VidPid, u.Name)),
            new[] { $"{usb.Count} USB-Geräte (ohne Hubs und Teil-Schnittstellen). Links VID:PID, rechts der vom Gerät gemeldete Name." });
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_hint == null) return;
        if (_drive.SelectedItem is DriveTarget t)
        {
            _hint.Text = t.IsSystem && !_allowSystem.Checked
                ? "⚠ Systemlaufwerk gewählt – Test nur möglich, wenn „Systemlaufwerk ausdrücklich zulassen“ angehakt ist."
                : $"Testdatei: {DriveBenchmark.TestFolder(t)}\\lzbench_<zufällig>.tmp · wird nach dem Test gelöscht, der Ordner ebenfalls, falls er leer ist.";
            _hint.ForeColor = t.IsSystem && !_allowSystem.Checked ? Theme.RedHot : Theme.SilverDim;
        }
        else
        {
            _hint.Text = "Kein Laufwerk gewählt – das Systemlaufwerk wird nie automatisch ausgewählt. Bei „Alles testen“ wird der Laufwerkstest ohne Auswahl übersprungen.";
            _hint.ForeColor = Theme.SilverDim;
        }
    }

    protected override void OnBusyChanged(bool anyBusy)
    {
        _drive.Enabled = _size.Enabled = _allowSystem.Enabled = _scan.Enabled = !anyBusy;
    }

    private IBenchmark? CreateBenchmark()
    {
        if (_drive.SelectedItem is not DriveTarget t)
        {
            Status?.Invoke("Laufwerkstest: bitte zuerst ein Laufwerk auswählen.", true);
            return null;
        }
        if (t.IsSystem && !_allowSystem.Checked)
        {
            Status?.Invoke($"Laufwerkstest: {t.Root} ist das Systemlaufwerk – nur nach ausdrücklicher Freigabe (Häkchen).", true);
            return null;
        }
        long mb = (_size.SelectedItem as SizeOption)?.Mb ?? 1024;
        var fresh = DriveCatalog.Enumerate().FirstOrDefault(d => d.Root == t.Root) ?? t;
        if (fresh.FreeBytes < (mb << 20) + Math.Max(1L << 30, fresh.TotalBytes / 50))
        {
            Status?.Invoke($"Laufwerkstest: zu wenig freier Speicher auf {t.Root} für {mb} MB (+ Reserve).", true);
            return null;
        }
        string msg =
            $"Schreibtest auf {t.Root}  ({t.Kind} · {t.Model})\n\n" +
            $"Es wird eine Testdatei mit {(mb >= 1024 ? $"{mb / 1024} GB" : $"{mb} MB")} angelegt:\n   {DriveBenchmark.TestFolder(t)}\\lzbench_<zufällig>.tmp\n\n" +
            "Schreibvorgänge: sequenziell schreiben, 4K zufällig schreiben (QD1 und QD32) – nur in diese Testdatei.\n" +
            "Bestehende Dateien werden nicht geöffnet, verändert oder gelöscht. Die Testdatei wird danach gelöscht.\n" +
            (t.IsSystem ? "\n⚠ ACHTUNG: Das ist das Systemlaufwerk (ausdrücklich freigegeben).\n" : "") +
            "\nSchreibtest jetzt starten?";
        bool confirmed = ConfirmDialog.Ask(FindForm(), "LAGZERO · Schreibtest bestätigen", msg.Replace("\nSchreibtest jetzt starten?", ""),
            "JA, SCHREIBTEST STARTEN", t.IsSystem);
        if (!confirmed)
        {
            Status?.Invoke("Laufwerkstest: nicht bestätigt – nichts geschrieben.", false);
            return null;
        }
        return new DriveBenchmark(fresh, mb << 20, _allowSystem.Checked) { Confirmed = true };
    }
}
