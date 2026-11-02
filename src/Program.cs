using System.Diagnostics;
using System.Globalization;

namespace Lagzero;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinTimer.timeBeginPeriod(1);
        try
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }

            if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
                return Headless(args);
            if (args.Length >= 2 && args[0].Equals("--analyze", StringComparison.OrdinalIgnoreCase))
                return AnalyzeLog(args[1]);
            if (args.Length >= 1 && args[0].Equals("--bench", StringComparison.OrdinalIgnoreCase))
                return BenchCli.Run(args);

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
        finally
        {
            WinTimer.timeEndPeriod(1);
        }
    }

    /// <summary>Writes unexpected errors to Logs\fehler.log and tells the user (instead of silently failing).</summary>
    private static void ReportCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Core.AppInfo.DataDir("Logs"), "fehler.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
            MessageBox.Show($"Unerwarteter Fehler:\n\n{ex?.Message}\n\nDetails: {path}", "LAGZERO Benchmark", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    /// <summary>Re-analyzes an existing polling2 / LAGZERO JSON log (raw_data[].delay) with our filter + stats.</summary>
    private static int AnalyzeLog(string path)
    {
        WinTimer.AttachConsole(-1);
        var inv = CultureInfo.InvariantCulture;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var d = doc.RootElement.GetProperty("raw_data").EnumerateArray()
            .Select(e => e.GetProperty("delay").GetDouble()).ToArray();
        var keep = Analysis.FilterOutliers(d, new FilterSettings());
        var f = Analysis.Compute(d.Where((_, i) => keep[i]).ToArray());
        var u = Analysis.Compute(d);
        foreach (var (n, s) in new[] { ("gefiltert", f), ("ungefiltert", u) })
            Console.WriteLine(string.Format(inv,
                "{0,-11} n={1,6}  Median {2:0.0000} ms ({3:0} Hz)  Mittel {4:0.0000} ms ({5:0} Hz)  min {6:0.0000}  max {7:0.0000}  Jitter {8:0.0000}  P99 {9:0.0000}",
                n, s.N, s.MedianMs, s.HzMedian, s.MeanMs, s.HzMean, s.MinMs, s.MaxMs, s.JitterMs, s.P99Ms));
        if (doc.RootElement.TryGetProperty("cleaned_data_count", out var cc))
            Console.WriteLine($"Log selbst: cleaned_data_count = {cc.GetInt32()}");
        return 0;
    }

    /// <summary>
    /// Command line mode for scripted tests:
    ///   --headless [--slot 1-4] [--packets N] [--drift] [--out file.json]
    /// </summary>
    private static int Headless(string[] args)
    {
        WinTimer.AttachConsole(-1);
        var inv = CultureInfo.InvariantCulture;
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        uint slot = uint.TryParse(Arg("--slot"), out var sl) && sl is >= 1 and <= 4 ? sl - 1 : 99;
        if (slot == 99)
            slot = Enumerable.Range(0, 4).Select(i => (uint)i).FirstOrDefault(XInput.IsConnected, 99u);
        if (slot == 99 || !XInput.IsConnected(slot))
        {
            Console.WriteLine("ERROR: kein XInput-Controller gefunden.");
            return 2;
        }
        int packets = int.TryParse(Arg("--packets"), out var p) ? p : 5000;
        var info = XInput.Describe(slot);
        Console.WriteLine($"Controller: Slot {slot + 1}  {info.Name}  {info.VidPid}");
        Console.WriteLine($"Timer-Aufloesung: {WinTimer.CurrentResolutionMs():0.###} ms, QPC {Stopwatch.Frequency} Hz");

        DriftResult? drift = null;
        if (args.Contains("--drift", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Drift-Test 5 s (Sticks loslassen) ...");
            drift = DriftTest.RunAsync(slot, 5, null, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var (n, s) in new[] { ("L", drift.Left), ("R", drift.Right) })
                Console.WriteLine(string.Format(inv, "  {0}: offset {1:0.00}%  max {2:0.00}%  noise {3:0.000}%  {4}  DZ {5}%",
                    n, s.OffsetPct, s.MaxRadiusPct, s.NoisePct, s.Verdict, s.RecommendedDeadzonePct));
        }

        Console.WriteLine($"Polling-Test: {packets} Pakete (Stick bewegen) ...");
        var engine = new PollingEngine();
        var cap = engine.RunAsync(slot, packets, CancellationToken.None).GetAwaiter().GetResult();
        if (cap.Error != null) Console.WriteLine("Hinweis: " + cap.Error);
        if (cap.Count < 3)
        {
            Console.WriteLine("ERROR: zu wenige Pakete.");
            return 3;
        }
        var res = Analysis.Analyze(cap);
        void Print(string name, IntervalStats s) =>
            Console.WriteLine(string.Format(inv,
                "  {0,-11} n={1,6}  Median {2:0.0000} ms ({3:0} Hz)  Mittel {4:0.0000} ms ({5:0} Hz)  min {6:0.0000}  max {7:0.0000}  Jitter {8:0.0000}  P99 {9:0.0000}",
                name, s.N, s.MedianMs, s.HzMedian, s.MeanMs, s.HzMean, s.MinMs, s.MaxMs, s.JitterMs, s.P99Ms));
        Print("gefiltert", res.Filtered);
        Print("ungefiltert", res.Unfiltered);
        Console.WriteLine(string.Format(inv, "  Ausreisser {0}, uebersprungene Pakete {1}, Loop {2:0} Abfragen/s, Dauer {3:0.000} s, Klasse {4} Hz",
            res.Outliers, cap.SkippedPackets, cap.PollsPerSecond, cap.DurationSec, res.RateClass));

        string path = Arg("--out") ?? Path.Combine(LogWriter.DefaultDirectory(), LogWriter.DefaultFileName(cap.StartedAt));
        LogWriter.Write(path, res, drift, info);
        Console.WriteLine("Log: " + path);
        return 0;
    }
}
