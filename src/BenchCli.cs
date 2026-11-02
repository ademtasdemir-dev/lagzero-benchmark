using System.Globalization;
using Lagzero.Core;

namespace Lagzero;

/// <summary>
/// Console mode for scripted runs:  --bench sys,cpu,ram,... [--out session.json] [--html report.html]
/// Write tests (drives) are never run from here unless --drive X: --allow-write is given explicitly.
/// </summary>
internal static class BenchCli
{
    public static int Run(string[] args)
    {
        WinTimer.AttachConsole(-1);
        var de = CultureInfo.GetCultureInfo("de-DE");
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var which = (args.Length > 1 ? args[1] : "sys,cpu,ram").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // --bench report --in lauf_x.json --html bericht.html : rebuild the HTML report of a saved run
        if (which.Contains("report") && Arg("--in") is { } inFile && Arg("--html") is { } outHtml)
        {
            var saved = Session.FromJson(File.ReadAllText(inFile)) ?? throw new InvalidDataException("Lauf nicht lesbar");
            File.WriteAllText(outHtml, HtmlReport.Build(saved));
            Console.WriteLine("HTML: " + outHtml);
            return 0;
        }
        if (which.Contains("gpumatch"))
        {
            var rules = Rules.Load(out _);
            var cases = new (string Name, double? Vram, bool Portable)[]
            {
                ("NVIDIA GeForce GTX 1080 Ti", 11, false), ("NVIDIA GeForce GTX 1080", 8, false), ("NVIDIA GeForce GTX 1070 Ti", 8, false),
                ("NVIDIA GeForce GTX 1070", 8, false), ("NVIDIA GeForce GTX 1060 3GB", 3, false), ("NVIDIA GeForce GTX 1060 6GB", 6, false),
                ("NVIDIA GeForce GTX 1060 5GB", 5, false), ("NVIDIA GeForce GTX 1050 Ti", 4, false), ("NVIDIA GeForce GTX 1050", 2, false),
                ("NVIDIA GeForce GTX 1050", 3, false), ("NVIDIA GeForce GT 1030", 2, false), ("NVIDIA GeForce RTX 2080 Ti", 11, false),
                ("NVIDIA GeForce RTX 2080 SUPER", 8, false), ("NVIDIA GeForce RTX 2080", 8, false), ("NVIDIA GeForce RTX 2070 SUPER", 8, false),
                ("NVIDIA GeForce RTX 2070", 8, false), ("NVIDIA GeForce RTX 2060 SUPER", 8, false), ("NVIDIA GeForce RTX 2060", 6, false),
                ("NVIDIA GeForce RTX 2060", 12, false), ("NVIDIA TITAN RTX", 24, false), ("NVIDIA GeForce RTX 4080", 16, false),
                ("NVIDIA GeForce RTX 4080 SUPER", 16, false), ("NVIDIA GeForce RTX 2080 with Max-Q Design", 8, false),
                ("NVIDIA GeForce GTX 1070 Max-Q", 8, false), ("NVIDIA GeForce RTX 4070 Laptop GPU", 8, false),
                ("NVIDIA GeForce GTX 1060 6GB", 6, true), ("NVIDIA GeForce GTX 1650", 4, false),
                ("NVIDIA GeForce RTX 4070 Ti SUPER", 16, false), ("NVIDIA GeForce RTX 4070 SUPER", 12, false), ("NVIDIA GeForce RTX 4070 Ti", 12, false),
                ("NVIDIA GeForce RTX 4070", 12, false), ("NVIDIA GeForce RTX 4060 Ti", 16, false), ("NVIDIA GeForce RTX 4080 Super", 16, false),
                ("NVIDIA GeForce RTX 3080 Ti", 12, false), ("NVIDIA GeForce RTX 3080", 10, false), ("NVIDIA GeForce RTX 3080", 12, false),
                ("NVIDIA GeForce RTX 3060", 8, false), ("NVIDIA GeForce RTX 5070 Ti", 16, false), ("NVIDIA GeForce RTX 2080 SUPER with Max-Q Design", 8, false),
            };
            foreach (var (n, v, p) in cases)
            {
                var (card, reason) = rules.Gpu.Match(n, v, p);
                Console.WriteLine($"{n,-45} {v,3} GB {(p ? "Notebook" : "        ")} -> " +
                    (card != null ? $"{card.Name} ({card.Shader} × {card.BoostTaktLautHerstellerMhz} MHz = {card.TflopsFp32} TFLOPS)" : "NICHT BEWERTBAR: " + reason));
            }
            return 0;
        }
        if (which.Contains("ramcheck"))
        {
            MemoryModule M(string slot, int type, int ff, int width = 64, string bank = "") => new(slot, 16L << 30, 0, 6000, "", "", type, ff, width, bank);
            var cases = new (string Name, List<MemoryModule> Mods)[]
            {
                ("DDR5 2 Riegel (Intel-Bezeichnung)", new() { M("Controller0-ChannelA-DIMM1", 34, 8), M("Controller1-ChannelA-DIMM1", 34, 8) }),
                ("DDR5 4 Riegel, 2 Kanäle", new() { M("Controller0-ChannelA-DIMM0", 34, 8), M("Controller0-ChannelA-DIMM1", 34, 8), M("Controller1-ChannelA-DIMM0", 34, 8), M("Controller1-ChannelA-DIMM1", 34, 8) }),
                ("DDR5 1 Riegel", new() { M("Controller0-ChannelA-DIMM1", 34, 8) }),
                ("DDR4 2 Riegel (AMD: DIMM 1 / P0 CHANNEL A,B)", new() { M("DIMM 1", 26, 8, 64, "P0 CHANNEL A"), M("DIMM 1", 26, 8, 64, "P0 CHANNEL B") }),
                ("DDR4 1 Riegel, Bezeichnung unlesbar", new() { M("DIMM_A2", 26, 8) }),
                ("DDR4 2 Riegel, Bezeichnung unlesbar", new() { M("DIMM_A2", 26, 8), M("DIMM_B2", 26, 8) }),
                ("Laptop LPDDR5 verlötet", new() { M("Controller0-ChannelA", 35, 0, 16), M("Controller0-ChannelB", 35, 0, 16), M("Controller1-ChannelA", 35, 0, 16), M("Controller1-ChannelB", 35, 0, 16) }),
                ("Laptop DDR4 verlötet (Bauform Row of chips)", new() { M("Controller0-ChannelA", 26, 23), M("Controller1-ChannelA", 26, 23) }),
                ("Laptop DDR5 SO-DIMM 2 Riegel", new() { M("Controller0-ChannelA-DIMM0", 34, 12), M("Controller1-ChannelA-DIMM0", 34, 12) }),
            };
            foreach (var (n, mods) in cases)
            {
                var (ch, basis) = SystemInfo.MemoryChannels(mods);
                Console.WriteLine($"{n,-48} -> {(ch is { } x ? $"{x} Kanal/Kanäle" : "NICHT BEWERTBAR")} | {basis}");
            }
            var real = SystemInfo.MemoryChannels(SystemInfo.MemoryModules());
            Console.WriteLine($"{"DIESES SYSTEM",-48} -> {(real.Channels is { } y ? $"{y} Kanal/Kanäle" : "NICHT BEWERTBAR")} | {real.Basis}");
            return 0;
        }
        var session = new Session();

        session.SystemInfo = SystemInfo.Collect();
        if (which.Contains("sys"))
            foreach (var g in session.SystemInfo.GroupBy(i => i.Section))
            {
                Console.WriteLine($"[{g.Key}]");
                foreach (var i in g) Console.WriteLine($"  {i.Label,-24} {i.Value}");
            }

        if (which.Contains("hw"))
        {
            var mon = Monitors.Primary();
            Console.WriteLine($"Hauptmonitor: {mon}");
            if (mon != null) foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3840, 2160) }) Console.WriteLine($"  {w}x{h}: {(mon.Supports(w, h) ? "unterstützt" : "NICHT unterstützt -> ausgegraut")}");
            foreach (var d in DriveCatalog.Enumerate())
            {
                var i = d.DiskNumber >= 0 ? DiskInterfaces.ForDisk(d.DiskNumber, d.BusId, d.MediaId) : DiskInterface.Unknown;
                Console.WriteLine($"{d.Root} {d.Model}: {i.ClassName} | {i.Detail} | schneller möglich: {i.DeviceCanBeFaster} | {i.DeviceNote}");
            }
        }
        foreach (var id in which.Where(w => w is not ("sys" or "hw")))
        {
            IBenchmark? b = BenchFactory.Create(id, args);
            if (b == null) { Console.WriteLine($"Unbekannter Test: {id}"); continue; }
            Console.WriteLine($"\n== {b.Title} ==");
            string last = "";
            var prog = new SyncProgress(p => { if (p.Text != last) { last = p.Text; Console.WriteLine($"  … {p.Text}"); } });
            var r = b.RunAsync(prog, CancellationToken.None).GetAwaiter().GetResult();
            session.Results[r.TestId] = r;
            foreach (var m in r.Metrics)
                Console.WriteLine($"  {m.Label,-44} {m.Value.ToString(m.Format, de),14} {m.Unit}");
            foreach (var n in r.Notes) Console.WriteLine("  · " + n);
            if (r.Error != null) Console.WriteLine("  FEHLER: " + r.Error);
            Console.WriteLine($"  Dauer {r.DurationSec:0.0} s");
        }

        if (Arg("--out") is { } outPath) { File.WriteAllText(outPath, session.ToJson()); Console.WriteLine("JSON: " + outPath); }
        if (Arg("--html") is { } htmlPath) { File.WriteAllText(htmlPath, HtmlReport.Build(session)); Console.WriteLine("HTML: " + htmlPath); }
        return 0;
    }

    /// <summary>IProgress that invokes synchronously on the reporting thread (console mode has no sync context).</summary>
    private sealed class SyncProgress(Action<BenchProgress> a) : IProgress<BenchProgress>
    {
        private readonly object _lock = new();
        public void Report(BenchProgress value) { lock (_lock) a(value); }
    }
}

public static class BenchFactory
{
    /// <summary>--res 1920x1080 (default: monitor resolution).</summary>
    private static RenderSettings CliRender(string[] args, bool vsync)
    {
        int i = Array.IndexOf(args, "--res");
        if (i >= 0 && i + 1 < args.Length && args[i + 1].Split('x', '×') is [var w, var h] && int.TryParse(w, out int iw) && int.TryParse(h, out int ih))
            return new RenderSettings(iw, ih, vsync, false);
        return RenderSettings.MonitorDefault(vsync);
    }

    public static IBenchmark? Create(string id, string[]? args = null)
    {
        args ??= Array.Empty<string>();
        string? Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (id.StartsWith("drive:", StringComparison.OrdinalIgnoreCase))
        {
            // console mode never writes without an explicit --allow-write; the system drive additionally needs --allow-system
            if (!args.Contains("--allow-write")) { Console.WriteLine("Laufwerkstest schreibt eine Testdatei: --allow-write angeben."); return null; }
            string letter = id[6..].TrimEnd(':', '\\').ToUpperInvariant();
            var t = DriveCatalog.Enumerate().FirstOrDefault(d => d.Letter == letter);
            if (t == null) { Console.WriteLine($"Laufwerk {letter}: nicht gefunden."); return null; }
            long mb = long.TryParse(Arg("--size"), out var s) ? s : 1024;
            double rnd = double.TryParse(Arg("--rnd"), System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 4;
            return new DriveBenchmark(t, mb << 20, args.Contains("--allow-system"), rnd) { Confirmed = true }; // --allow-write = confirmation
        }
        return id switch
        {
            "cpu" => new CpuBenchmark(),
            "ram" => new RamBenchmark(),
            "gpu" => new GpuBenchmark(CliRender(args, false)),
            "frametime" => new FrametimeBenchmark(CliRender(args, args.Contains("--vsync"))),
            // M-Lab publishes IP/date/time of every test → only with explicit --consent-mlab
            "internet" when args.Contains("--consent-mlab") => new InternetBenchmark(),
            _ => null,
        };
    }
}
