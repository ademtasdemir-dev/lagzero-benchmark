using System.Diagnostics;
using Lagzero.Core;
using Lagzero.UI;

namespace Lagzero;

/// <summary>Controller polling / drift / live tab (the original LAGZERO Controller Benchmark).</summary>
internal sealed class ControllerPage : UserControl
{
    public event Action<string, bool>? StatusChanged;
    public event Action<BenchResult>? ResultReady;
    public event Action? BusyChanged;
    private bool _globalBusy, _lastBusy;

    private readonly ComboBox _slot = new();
    private readonly ComboBox _packets = new();
    private readonly LzButton _btnStart = new("POLLING-TEST STARTEN", primary: true);
    private readonly LzButton _btnDrift = new("DRIFT-TEST · 5 S");
    private readonly LzButton _btnSave = new("LOG SPEICHERN");
    private readonly LzButton _btnFolder = new("LOG-ORDNER");
    private readonly LzButton _btnRefresh = new("SCAN");
    private readonly ProgressView _progress = new();
    private readonly LiveView _live = new();
    private readonly ResultsView _results = new();
    private readonly ChartView _chart = new();
    private readonly DriftView _drift = new();
    private readonly Card _resCard = new("ERGEBNIS");
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 16 };

    private readonly PollingEngine _engine = new();
    private CancellationTokenSource? _cts;
    private bool _pollingRunning, _driftRunning;
    private PollingResult? _lastPoll;
    private DriftResult? _lastDrift;
    private ControllerInfo? _lastInfo;
    private int _requested;
    private int _liveLastCount; private long _liveLastTs; private double _liveHz;
    private int _refreshTick;

    public ControllerPage()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.Silver;
        Dock = DockStyle.Fill;

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10, 8, 10, 8),
            BackColor = Theme.Bg,
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        // ---- left column
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = Theme.Bg };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 268));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var ctrlCard = new Card("TEST-STEUERUNG") { Dock = DockStyle.Fill };
        ctrlCard.Controls.Add(BuildControls());
        var liveCard = new Card("LIVE-EINGABE") { Dock = DockStyle.Fill };
        _live.Dock = DockStyle.Fill;
        liveCard.Controls.Add(_live);
        left.Controls.Add(ctrlCard, 0, 0);
        left.Controls.Add(liveCard, 0, 1);

        // ---- right column
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, BackColor = Theme.Bg };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        _resCard.Dock = DockStyle.Fill;
        _results.Dock = DockStyle.Fill;
        _resCard.Controls.Add(_results);
        var chartCard = new Card("INTERVALL-VERLAUF") { Dock = DockStyle.Fill };
        _chart.Dock = DockStyle.Fill;
        chartCard.Controls.Add(_chart);
        var driftCard = new Card("STICK-DRIFT") { Dock = DockStyle.Fill };
        _drift.Dock = DockStyle.Fill;
        driftCard.Controls.Add(_drift);
        right.Controls.Add(_resCard, 0, 0);
        right.Controls.Add(chartCard, 0, 1);
        right.Controls.Add(driftCard, 0, 2);

        main.Controls.Add(left, 0, 0);
        main.Controls.Add(right, 1, 0);

        Controls.Add(main);

        _btnStart.Click += async (_, _) => await TogglePolling();
        _btnDrift.Click += async (_, _) => await RunDrift();
        _btnSave.Click += (_, _) => SaveAs();
        _btnFolder.Click += (_, _) => OpenFolder();
        _btnRefresh.Click += (_, _) => RefreshSlots(force: true);
        _ui.Tick += (_, _) => UiTick();

        RefreshSlots(force: true);
        UpdateButtons();
        _ui.Start();
    }

    public bool IsBusy => _pollingRunning || _driftRunning;

    /// <summary>Another test (outside this tab) is running: lock the start buttons.</summary>
    public void SetGlobalBusy(bool busy) { _globalBusy = busy; UpdateButtons(); }

    /// <summary>Live view only needs fast updates while visible.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!_pollingRunning) _ui.Interval = Visible ? 16 : 500;
    }

    public void CancelRunning() => _cts?.Cancel();

    private void EmitResult()
    {
        var info = _lastPoll?.Capture.Controller ?? _lastInfo;
        var r = new BenchResult
        {
            TestId = "controller",
            Title = "Controller",
            StartedAt = _lastPoll?.Capture.StartedAt ?? _lastDrift?.StartedAt ?? DateTime.Now,
        };
        if (info != null) r.Notes.Add($"{info.Name} · Slot {info.Index + 1} · {info.VidPid}");
        if (_lastPoll is { } p)
        {
            var f = p.Filtered; var u = p.Unfiltered;
            r.DurationSec += p.Capture.DurationSec;
            r.Add("ctl.hz.median", "Polling-Rate (Median)", f.HzMedian, "Hz", true, "N0", "Polling (gefiltert)");
            r.Add("ctl.hz.mean", "Polling-Rate (Mittelwert)", f.HzMean, "Hz", true, "N0", "Polling (gefiltert)");
            r.Add("ctl.int.median", "Abstand Median", f.MedianMs, "ms", false, "0.000", "Polling (gefiltert)");
            r.Add("ctl.int.min", "Abstand min", f.MinMs, "ms", false, "0.000", "Polling (gefiltert)");
            r.Add("ctl.int.mean", "Abstand Ø", f.MeanMs, "ms", false, "0.000", "Polling (gefiltert)");
            r.Add("ctl.int.max", "Abstand max", f.MaxMs, "ms", false, "0.000", "Polling (gefiltert)");
            r.Add("ctl.jitter", "Jitter (Std.-Abw.)", f.JitterMs, "ms", false, "0.000", "Polling (gefiltert)");
            r.Add("ctl.p99", "99-%-Wert", f.P99Ms, "ms", false, "0.000", "Polling (gefiltert)");
            r.Add("ctl.u.hz.mean", "Polling-Rate (Mittelwert)", u.HzMean, "Hz", true, "N0", "Polling (ungefiltert)");
            r.Add("ctl.u.int.max", "Abstand max", u.MaxMs, "ms", false, "0.000", "Polling (ungefiltert)");
            r.Add("ctl.u.jitter", "Jitter (Std.-Abw.)", u.JitterMs, "ms", false, "0.000", "Polling (ungefiltert)");
            r.Add("ctl.outliers", "Ausreißer entfernt", p.Outliers, "", false, "N0", "Polling (ungefiltert)");
            r.Series = p.IntervalsMs; r.SeriesLabel = "Paket-Intervalle (ms)";
        }
        if (_lastDrift is { } d)
        {
            r.DurationSec += d.Seconds;
            r.Add("ctl.drift.l", "Linker Stick · max. Auslenkung", d.Left.MaxRadiusPct, "%", false, "0.00", "Stick-Drift");
            r.Add("ctl.drift.r", "Rechter Stick · max. Auslenkung", d.Right.MaxRadiusPct, "%", false, "0.00", "Stick-Drift");
            r.Notes.Add($"Drift: links {d.Left.Verdict} (Deadzone ≥ {d.Left.RecommendedDeadzonePct} %), rechts {d.Right.Verdict} (Deadzone ≥ {d.Right.RecommendedDeadzonePct} %).");
        }
        ResultReady?.Invoke(r);
    }

    private Control BuildControls()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, BackColor = Theme.Panel, Margin = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label L(string s) => new()
        {
            Text = s, ForeColor = Theme.SilverDim, Font = Theme.Display(8.5f, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel,
        };
        foreach (var cb in new[] { _slot, _packets })
        {
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.FlatStyle = FlatStyle.Flat;
            cb.BackColor = Theme.PanelHi;
            cb.ForeColor = Theme.Silver;
            cb.Font = Theme.Ui(9.5f);
            cb.Dock = DockStyle.Fill;
            cb.Margin = new Padding(3, 5, 3, 3);
        }
        foreach (var n in new[] { 1000, 2000, 5000, 10000, 20000 })
            _packets.Items.Add(new PacketOption(n));
        _packets.SelectedIndex = 2;

        t.Controls.Add(L("CONTROLLER"), 0, 0);
        t.Controls.Add(_slot, 1, 0);
        _btnRefresh.Dock = DockStyle.Fill; _btnRefresh.Margin = new Padding(3, 4, 0, 3);
        t.Controls.Add(_btnRefresh, 2, 0);

        t.Controls.Add(L("PAKETE"), 0, 1);
        t.Controls.Add(_packets, 1, 1);
        t.SetColumnSpan(_packets, 2);

        var row3 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Panel };
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        _btnStart.Dock = DockStyle.Fill; _btnDrift.Dock = DockStyle.Fill;
        _btnStart.Margin = new Padding(0, 6, 4, 2); _btnDrift.Margin = new Padding(4, 6, 0, 2);
        row3.Controls.Add(_btnStart, 0, 0); row3.Controls.Add(_btnDrift, 1, 0);
        t.Controls.Add(row3, 0, 2); t.SetColumnSpan(row3, 3);

        var row4 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Panel };
        row4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _btnSave.Dock = DockStyle.Fill; _btnFolder.Dock = DockStyle.Fill;
        _btnSave.Margin = new Padding(0, 4, 4, 2); _btnFolder.Margin = new Padding(4, 4, 0, 2);
        row4.Controls.Add(_btnSave, 0, 0); row4.Controls.Add(_btnFolder, 1, 0);
        t.Controls.Add(row4, 0, 3); t.SetColumnSpan(row4, 3);

        _progress.Dock = DockStyle.Fill; _progress.Margin = new Padding(0, 10, 0, 0);
        t.Controls.Add(_progress, 0, 4); t.SetColumnSpan(_progress, 3);
        return t;
    }

    private sealed record PacketOption(int N)
    {
        public override string ToString() => $"{N.ToString("N0", Theme.De)} Pakete";
    }

    private sealed record SlotOption(uint Index, ControllerInfo? Info)
    {
        public override string ToString() => Info != null
            ? $"Slot {Index + 1} · {Info.Name}" + (Info.Vid != 0 ? $"  ({Info.Vid:X4}:{Info.Pid:X4})" : "")
            : $"Slot {Index + 1} · nicht verbunden";
    }

    private uint SelectedSlot => (_slot.SelectedItem as SlotOption)?.Index ?? 0;

    private void RefreshSlots(bool force)
    {
        var opts = Enumerable.Range(0, 4).Select(i => (uint)i)
            .Select(i => new SlotOption(i, XInput.IsConnected(i) ? XInput.Describe(i) : null)).ToList();
        bool same = !force && _slot.Items.Count == 4 &&
                    opts.Select((o, i) => o.Equals(_slot.Items[i])).All(x => x);
        if (same) return;
        uint prev = SelectedSlot;
        bool hadSel = _slot.SelectedItem is SlotOption { Info: not null };
        _slot.BeginUpdate();
        _slot.Items.Clear();
        foreach (var o in opts) _slot.Items.Add(o);
        int idx = hadSel && opts[(int)prev].Info != null ? (int)prev : opts.FindIndex(o => o.Info != null);
        _slot.SelectedIndex = Math.Max(0, idx);
        _slot.EndUpdate();
    }

    private bool Busy => _pollingRunning || _driftRunning;

    private void UpdateButtons()
    {
        _btnStart.Text = _pollingRunning ? "ABBRECHEN" : "POLLING-TEST STARTEN";
        _btnStart.Enabled = _pollingRunning || (!_driftRunning && !_globalBusy);
        _btnDrift.Text = _driftRunning ? "ABBRECHEN" : "DRIFT-TEST · 5 S";
        _btnDrift.Enabled = _driftRunning || (!_pollingRunning && !_globalBusy);
        _btnSave.Enabled = !Busy && (_lastPoll != null || _lastDrift != null);
        _slot.Enabled = _packets.Enabled = _btnRefresh.Enabled = !Busy;
        if (Busy != _lastBusy) { _lastBusy = Busy; BusyChanged?.Invoke(); }
    }

    private void UiTick()
    {
        if (_pollingRunning)
        {
            int c = _engine.Progress;
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _liveLastTs) / (double)Stopwatch.Frequency;
            if (dt >= 0.25)
            {
                if (c > _liveLastCount && _liveLastTs != 0) _liveHz = (c - _liveLastCount) / dt;
                _liveLastCount = c; _liveLastTs = now;
                _results.LiveHz = _liveHz;
                _results.Invalidate();
            }
            _progress.Value = _requested > 0 ? c / (double)(_requested + 1) : 0;
            _progress.Status = c == 0 ? "WARTE AUF INPUT – STICK KREISEN LASSEN …"
                : $"MESSUNG LÄUFT · {Math.Max(0, c - 1).ToString("N0", Theme.De)} / {_requested.ToString("N0", Theme.De)} INTERVALLE";
            _live.SetState(_engine.Latest, true);
            return;
        }

        // idle / drift: read the controller directly for the live view
        uint slot = SelectedSlot;
        bool ok = XInput.GetState(slot, out var s) == XInput.Success;
        _live.SetState(s, ok);

        if (!Busy && ++_refreshTick >= 120) // ~2 s
        {
            _refreshTick = 0;
            RefreshSlots(force: false);
        }
    }

    private async Task TogglePolling()
    {
        if (_pollingRunning) { _cts?.Cancel(); return; }
        uint slot = SelectedSlot;
        if (!XInput.IsConnected(slot)) { SetStatus("Kein Controller in diesem Slot verbunden.", error: true); return; }

        _requested = (_packets.SelectedItem as PacketOption)?.N ?? 5000;
        _cts = new CancellationTokenSource();
        _pollingRunning = true;
        _liveLastCount = 0; _liveLastTs = 0; _liveHz = 0;
        _results.Measuring = true; _results.LiveHz = 0;
        _results.SetResult(null);
        _ui.Interval = 50; // keep the UI thread quiet while measuring
        UpdateButtons();
        SetStatus("Polling-Test läuft – Stick gleichmäßig kreisen lassen (Esc = Abbrechen).");

        try
        {
            var cap = await _engine.RunAsync(slot, _requested, _cts.Token);
            _lastInfo = cap.Controller;
            if (cap.Count < 3)
            {
                _results.Measuring = false;
                _results.SetResult(null);
                _progress.Value = 0; _progress.Status = "KEINE DATEN";
                SetStatus(cap.Error ?? "Zu wenige Pakete empfangen.", error: true);
                return;
            }
            var res = Analysis.Analyze(cap);
            _lastPoll = res;
            _results.Measuring = false;
            _results.SetResult(res);
            _chart.SetResult(res);
            _resCard.Badge = cap.Controller.Name.ToUpperInvariant();
            _resCard.Invalidate();
            _progress.Value = 1;
            _progress.Status = cap.Error != null ? "UNVOLLSTÄNDIG" : cap.Cancelled ? "ABGEBROCHEN" : "FERTIG";

            string path = Path.Combine(LogWriter.DefaultDirectory(), LogWriter.DefaultFileName(cap.StartedAt));
            LogWriter.Write(path, res, _lastDrift, cap.Controller);
            SetStatus($"Fertig: {Theme.Hz(res.Filtered.HzMedian)} (Median) · {Theme.Hz(res.Filtered.HzMean)} (Mittelwert) · Log: {path}",
                error: cap.Error != null);
            EmitResult();
        }
        catch (Exception ex)
        {
            _results.Measuring = false; _results.SetResult(_lastPoll);
            SetStatus("Fehler: " + ex.Message, error: true);
        }
        finally
        {
            _pollingRunning = false;
            _ui.Interval = Visible ? 16 : 500;
            UpdateButtons();
        }
    }

    private async Task RunDrift()
    {
        if (_driftRunning) { _cts?.Cancel(); return; }
        uint slot = SelectedSlot;
        if (!XInput.IsConnected(slot)) { SetStatus("Kein Controller in diesem Slot verbunden.", error: true); return; }
        _driftRunning = true;
        _cts = new CancellationTokenSource();
        UpdateButtons();
        try
        {
            for (int i = 3; i > 0; i--)
            {
                _progress.Value = 0;
                _progress.Status = $"STICKS LOSLASSEN · START IN {i} …";
                await Task.Delay(600, _cts.Token);
            }
            _progress.Status = "DRIFT-TEST LÄUFT · STICKS NICHT BERÜHREN";
            SetStatus("Drift-Test läuft (5 s) – Controller ablegen, Sticks nicht berühren.");
            var prog = new Progress<double>(p => _progress.Value = p);
            var res = await DriftTest.RunAsync(slot, 5.0, prog, _cts.Token);
            _lastDrift = res;
            _lastInfo ??= XInput.Describe(slot);
            _drift.SetResult(res);
            _progress.Status = "DRIFT-TEST FERTIG";
            SetStatus($"Drift-Test: links {res.Left.Verdict} ({res.Left.MaxRadiusPct.ToString("0.00", Theme.De)} %), " +
                      $"rechts {res.Right.Verdict} ({res.Right.MaxRadiusPct.ToString("0.00", Theme.De)} %) · {res.Samples:N0} Samples · " +
                      "Mit LOG SPEICHERN zusammen mit dem Polling-Ergebnis sichern.");
            EmitResult();
        }
        catch (OperationCanceledException)
        {
            _progress.Value = 0; _progress.Status = "ABGEBROCHEN";
            SetStatus("Drift-Test abgebrochen.");
        }
        catch (Exception ex)
        {
            SetStatus("Drift-Test fehlgeschlagen: " + ex.Message, error: true);
        }
        finally
        {
            _driftRunning = false;
            UpdateButtons();
        }
    }

    private void SaveAs()
    {
        var info = _lastPoll?.Capture.Controller ?? _lastInfo ?? XInput.Describe(SelectedSlot);
        using var dlg = new SaveFileDialog
        {
            Filter = "JSON-Log (*.json)|*.json",
            InitialDirectory = LogWriter.DefaultDirectory(),
            FileName = LogWriter.DefaultFileName(_lastPoll?.Capture.StartedAt ?? DateTime.Now),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            LogWriter.Write(dlg.FileName, _lastPoll, _lastDrift, info);
            SetStatus("Log gespeichert: " + dlg.FileName);
        }
        catch (Exception ex) { SetStatus("Speichern fehlgeschlagen: " + ex.Message, error: true); }
    }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogWriter.DefaultDirectory()}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private void SetStatus(string text, bool error = false) => StatusChanged?.Invoke(text, error);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _ui.Stop(); _ui.Dispose(); }
        base.Dispose(disposing);
    }
}