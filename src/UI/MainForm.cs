using Lagzero.Core;
using Lagzero.UI;

namespace Lagzero;

internal sealed class MainForm : Form
{
    private readonly HeaderView _header = new();
    private readonly TabStrip _tabs = new();
    private readonly LzButton _btnAll = new("ALLES TESTEN", primary: true);
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly Label _status = new();
    private readonly Dictionary<string, Control> _pages = new();

    private readonly OverviewPage _overview = new();
    private readonly SystemPage _system = new();
    private readonly HistoryPage _history = new();
    private readonly ControllerPage _controller = new();
    private readonly List<TestPage> _testPages = new(); // in "Alles testen" order

    private Session _session = new();
    private CancellationTokenSource? _cts;
    private TestPage? _running;
    private bool _allRunning;

    public MainForm()
    {
        Text = "LAGZERO Benchmark";
        BackColor = Theme.Bg;
        ForeColor = Theme.Silver;
        Font = Theme.Ui(9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 860);
        MinimumSize = new Size(1060, 760);
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _header.RightTitle = "BENCHMARK";
        _header.RightSub = $"v{AppInfo.Version}  ·  System · CPU · RAM · GPU · Laufwerke · Controller";
        _header.Dock = DockStyle.Top;
        _header.Height = 96;

        var tabRow = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        _tabs.Dock = DockStyle.Fill;
        _btnAll.Dock = DockStyle.Right; _btnAll.Width = 170;
        var allPad = new Panel { Dock = DockStyle.Right, Width = 186, Padding = new Padding(0, 4, 16, 6), BackColor = Theme.Bg };
        allPad.Controls.Add(_btnAll);
        tabRow.Controls.Add(_tabs); tabRow.Controls.Add(allPad);

        _status.Dock = DockStyle.Bottom; _status.Height = 24;
        _status.BackColor = Theme.Panel; _status.ForeColor = Theme.SilverDim;
        _status.Padding = new Padding(10, 4, 10, 0); _status.Font = Theme.Ui(8.5f);

        Controls.Add(_host); Controls.Add(tabRow); Controls.Add(_status); Controls.Add(_header);

        // ---- pages
        AddPage("overview", "ÜBERSICHT", _overview);
        AddPage("system", "SYSTEM", _system);
        AddTestPage(new TestPage("cpu", "CPU-BENCHMARK",
            "Ganzzahl, Fließkomma und SIMD (AVX/FMA) – jeweils auf einem Kern und auf allen Kernen. Jeder Test rechnet eine feste " +
            "Arbeitsmenge; gemessen wird die Dauer, die Punktzahl ist der Durchsatz (1 Punkt = 1 Mio. Operationen/s). Dauer ca. 15–30 s.")
        { Factory = () => new CpuBenchmark() }, "CPU");
        AddTestPage(new TestPage("ram", "RAM-BENCHMARK",
            "Lesen, Schreiben und Kopieren mit allen Threads über Puffer weit größer als jeder CPU-Cache (MB/s), dazu die " +
            "Zugriffslatenz per zufälliger Zeigerkette (ns). Zeigt außerdem ungenutzten / reservierten RAM, freie Steckplätze und Takt-Hinweise.")
        { Factory = () => new RamBenchmark() }, "RAM");
        foreach (var (id, title, page) in ExtraPages.Create(this))
            AddTestPage(page, title);
        AddPage("controller", "CONTROLLER", _controller);
        AddPage("history", "VERLAUF", _history);

        _tabs.IsActive = id => _running?.TestId == id || (id == "controller" && _controller.IsBusy);
        _tabs.SelectedChanged += ShowPage;
        _btnAll.Click += async (_, _) => await RunAll();

        _overview.ExportJson += ExportJson;
        _overview.ExportHtml += ExportHtml;
        _overview.NewRun += () => { if (!Busy) NewSession(); };
        _system.RefreshRequested += async () => await LoadSystemInfo();
        _controller.StatusChanged += SetStatus;
        _controller.ResultReady += Store;
        _controller.BusyChanged += UpdateBusy;

        _tabs.Select("overview");
        _overview.ShowSession(_session);
        _history.Reload();
        SetStatus($"Bereit · Prozesspriorität: {SafePriority()} · Timer-Auflösung: {WinTimer.CurrentResolutionMs().ToString("0.###", Theme.De)} ms · Daten: {AppInfo.DataDir("")}");
        Shown += async (_, _) =>
        {
            await LoadSystemInfo();
            await RunStartupArgs(Environment.GetCommandLineArgs());
        };
    }

    /// <summary>Optional: --tab id  and/or  --ui-run id1,id2 (start tests after launch, for scripted checks).</summary>
    private async Task RunStartupArgs(string[] args)
    {
        string? Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Arg("--res") is { } res0 && res0.Split('x') is [var rw0, var rh0] && int.TryParse(rw0, out int w0) && int.TryParse(rh0, out int h0))
        {
            ExtraPages.GpuOptions?.Select(w0, h0);
            ExtraPages.FrametimeOptions?.Select(w0, h0);
        }
        if (Arg("--drive") is { } letter && _testPages.OfType<DrivePage>().FirstOrDefault() is { } dp)
            await dp.SelectAsync(letter);
        if (Arg("--ui-run") is { } ids)
            foreach (var id in ids.Split(','))
                if (_testPages.FirstOrDefault(p => p.TestId == id) is { } page) { _tabs.Select(id); await RunTest(page); }
        if (args.Contains("--run-all")) await RunAll();
        if (Arg("--tab") is { } tab && _pages.ContainsKey(tab)) _tabs.Select(tab);
    }

    private bool Busy => _running != null || _allRunning || _controller.IsBusy;

    private void AddPage(string id, string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages[id] = page;
        _host.Controls.Add(page);
        _tabs.Add(id, title);
    }

    private void AddTestPage(TestPage page, string title)
    {
        page.Status = SetStatus;
        page.StartRequested += async p => await RunTest(p);
        page.CancelRequested += () => _cts?.Cancel();
        _testPages.Add(page);
        AddPage(page.TestId, title, page);
    }

    private void ShowPage(string id)
    {
        foreach (var (k, p) in _pages) p.Visible = k == id;
        if (id == "history") _history.Reload();
    }

    private async Task LoadSystemInfo()
    {
        SetStatus("System-Informationen werden gelesen (WMI) …");
        var info = await Task.Run(SystemInfo.Collect);
        _session.SystemInfo = info;
        _system.ShowInfo(info);
        _overview.ShowSession(_session);
        SetStatus($"System erkannt: {info.FirstOrDefault(i => i.Label == "Prozessor")?.Value} · {info.Count(i => i.Section == "GPU")} GPU(s) · " +
                  $"{info.FirstOrDefault(i => i.Label == "Gesamt (installiert)")?.Value} RAM");
    }

    private void NewSession()
    {
        var info = _session.SystemInfo;
        _session = new Session { SystemInfo = info };
        foreach (var p in _testPages) { p.ShowResult(null); p.ReportProgress(new BenchProgress(0, "Bereit")); }
        _overview.ShowSession(_session);
        SetStatus("Neuer Lauf gestartet – Ergebnisse werden ab jetzt in einem neuen Verlaufseintrag gespeichert.");
    }

    /// <summary>Runs one benchmark page. Returns null if it did not start (no confirmation, busy ...).</summary>
    private async Task<BenchResult?> RunTest(TestPage page)
    {
        if (_running != null || _controller.IsBusy) return null;
        IBenchmark? bench;
        try { bench = page.Factory(); }
        catch (Exception ex) { SetStatus(ex.Message, true); return null; }
        if (bench == null) return null;
        if (_closing || IsDisposed) return null;          // window is closing – never start (especially no write test)

        _running = page;
        _cts ??= new CancellationTokenSource();
        UpdateBusy();
        SetStatus($"{bench.Title}-Test läuft … (Esc oder ABBRECHEN zum Abbrechen)");
        var progress = new Progress<BenchProgress>(page.ReportProgress);
        try
        {
            var r = await bench.RunAsync(progress, _cts.Token);
            page.ShowResult(r);
            if (!r.Cancelled) Store(r);   // partial results are shown but never enter the run history
            SetStatus(r.Cancelled ? $"{bench.Title} · abgebrochen." :
                      r.Error != null ? $"{bench.Title} · {r.Error}" : $"{bench.Title} · fertig in {r.DurationSec.ToString("0.0", Theme.De)} s · im Verlauf gespeichert.",
                      r.Error != null);
            return r;
        }
        catch (OperationCanceledException)
        {
            page.ReportProgress(new BenchProgress(0, "Abgebrochen"));
            SetStatus($"{bench.Title} · abgebrochen.");
            return null;
        }
        catch (Exception ex)
        {
            string msg = Explain(ex);
            page.ReportProgress(new BenchProgress(0, msg.StartsWith("Auf diesem") ? "Nicht verfügbar" : "Fehler"));
            page.ShowResult(new BenchResult { TestId = bench.Id, Title = bench.Title, Error = msg });
            SetStatus($"{bench.Title} · {msg}", true);
            return null;
        }
        finally
        {
            _running = null;
            if (!_allRunning) { _cts?.Dispose(); _cts = null; }
            UpdateBusy();
            if (_closing && !_allRunning) BeginInvoke(Close);   // finish the deferred close after cleanup
        }
    }

    /// <summary>Missing hardware / instruction sets / system components → "not available" instead of a crash.</summary>
    internal static string Explain(Exception ex)
    {
        var e = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;
        string type = e.GetType().FullName ?? "";
        bool unavailable = e is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or NotSupportedException
                               or System.Runtime.InteropServices.COMException or OutOfMemoryException
                           || type.StartsWith("SharpGen.", StringComparison.Ordinal);
        string detail = e switch
        {
            DllNotFoundException => "eine benötigte Systemkomponente fehlt",
            OutOfMemoryException => "zu wenig freier Arbeitsspeicher für den Test",
            _ when type.StartsWith("SharpGen.") || e is System.Runtime.InteropServices.COMException => "keine passende Direct3D-11-Grafikkarte bzw. kein Treiber",
            _ => e.Message,
        };
        return unavailable ? $"Auf diesem System nicht verfügbar: {detail}." : $"Fehler: {e.Message}";
    }

    private async Task RunAll()
    {
        if (_allRunning) { _cts?.Cancel(); return; }
        if (Busy) return;
        NewSession();
        if (_session.SystemInfo.Count == 0) await LoadSystemInfo();
        _allRunning = true;
        _cts = new CancellationTokenSource();
        UpdateBusy();
        var skipped = new List<string>();
        try
        {
            foreach (var page in _testPages.Where(p => !p.ExcludeFromAll))   // internet test: own click only
            {
                if (_cts.IsCancellationRequested) break;
                _tabs.Select(page.TestId);
                var r = await RunTest(page);
                if (r == null && !_cts.IsCancellationRequested) skipped.Add(page.TestId == "drive" ? "Laufwerke (kein Laufwerk gewählt oder nicht bestätigt)" : page.TestId.ToUpperInvariant());
            }
        }
        finally
        {
            bool cancelled = _cts.IsCancellationRequested;
            _allRunning = false;
            _cts.Dispose(); _cts = null;
            UpdateBusy();
            _tabs.Select("overview");
            SetStatus(cancelled ? "Alles testen: abgebrochen." :
                $"Alles testen: fertig · {_session.Results.Count} Test(s)" + (skipped.Count > 0 ? $" · übersprungen: {string.Join(", ", skipped)}" : "") +
                " · Controller-Test bitte separat (Stick-Bewegung nötig).");
        }
    }

    private void Store(BenchResult r)
    {
        _session.Results[r.TestId] = r;
        try { HistoryStore.Save(_session); } catch (Exception ex) { SetStatus("Verlauf konnte nicht gespeichert werden: " + ex.Message, true); }
        _overview.ShowSession(_session);
    }

    private void UpdateBusy()
    {
        bool busy = Busy;
        foreach (var p in _testPages) p.SetBusy(p == _running, busy);
        _controller.SetGlobalBusy(_running != null || _allRunning);
        _btnAll.Text = _allRunning ? "ABBRECHEN" : "ALLES TESTEN";
        _btnAll.Enabled = _allRunning || !busy;
        _tabs.Invalidate();
    }

    private void ExportJson()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json", FileName = $"lagzero_benchmark_{_session.Id}.json",
            InitialDirectory = AppInfo.DataDir("Berichte"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, HtmlReport.ForExport(_session, _overview.IncludeDevices).ToJson());
        SetStatus("JSON gespeichert: " + dlg.FileName);
    }

    private void ExportHtml()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "HTML-Bericht (*.html)|*.html", FileName = $"lagzero_bericht_{_session.Id}.html",
            InitialDirectory = AppInfo.DataDir("Berichte"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, HtmlReport.Build(_session, null, _overview.IncludeDevices));
        SetStatus("HTML-Bericht gespeichert: " + dlg.FileName);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
    }

    public void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? Theme.RedHot : Theme.SilverDim;
    }

    private static string SafePriority()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().PriorityClass.ToString(); } catch { return "?"; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            _cts?.Cancel();
            _controller.CancelRunning();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private bool _closing;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _cts?.Cancel();
        _controller.CancelRunning();
        // a test is still cleaning up (e.g. deleting its drive test file): close once it has finished
        if (_running != null) { e.Cancel = true; SetStatus("Test wird abgebrochen und aufgeräumt – Fenster schließt danach automatisch …"); }
        base.OnFormClosing(e);
    }
}
