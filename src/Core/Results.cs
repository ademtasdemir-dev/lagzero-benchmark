using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lagzero.Core;

/// <summary>One measured value. Only measured numbers – no reference / database values.</summary>
public sealed record Metric(
    string Key,
    string Label,
    double Value,
    string Unit,
    bool HigherIsBetter = true,
    string Format = "N0",
    string? Group = null);

public readonly record struct BenchProgress(double Fraction, string Text);

public sealed class BenchResult
{
    public string TestId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public double DurationSec { get; set; }
    public List<Metric> Metrics { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    /// <summary>Test conditions and detected hardware facts (resolution, refresh rate, VSync, interface class …).</summary>
    public Dictionary<string, string> Info { get; set; } = new();
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    /// <summary>Optional time series (e.g. frame times in ms) for charts.</summary>
    public double[]? Series { get; set; }
    public string? SeriesLabel { get; set; }

    public void Add(string key, string label, double value, string unit, bool higherIsBetter = true, string fmt = "N0", string? group = null)
        => Metrics.Add(new Metric(key, label, value, unit, higherIsBetter, fmt, group));
}

public interface IBenchmark
{
    string Id { get; }
    string Title { get; }
    Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct);
}

public sealed record InfoItem(string Section, string Label, string Value);

/// <summary>A benchmark run: system info + results of every test that ran.</summary>
public sealed class Session
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public string Program { get; set; } = "LAGZERO Benchmark";
    public string Version { get; set; } = AppInfo.Version;
    public List<InfoItem> SystemInfo { get; set; } = new();
    public Dictionary<string, BenchResult> Results { get; set; } = new();

    public string Summary =>
        $"{StartedAt:dd.MM.yyyy HH:mm} · {Results.Count} Test(s) · {string.Join(", ", Results.Values.Select(r => r.Title))}";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static Session? FromJson(string json) => JsonSerializer.Deserialize<Session>(json, Json);
}

public static class AppInfo
{
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0";

    /// <summary>Data root next to the exe (portable), falls back to Documents if not writable.</summary>
    public static string DataDir(string sub)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LAGZERO") })
        {
            try
            {
                string dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch { }
        }
        return Path.GetTempPath();
    }
}

/// <summary>Stores every run as JSON in the "Verlauf" folder.</summary>
public static class HistoryStore
{
    public static string Dir => AppInfo.DataDir("Verlauf");

    public static string Save(Session s)
    {
        string path = Path.Combine(Dir, $"lauf_{s.Id}.json");
        File.WriteAllText(path, s.ToJson());
        return path;
    }

    public static List<(string Path, Session Session)> LoadAll()
    {
        var list = new List<(string, Session)>();
        foreach (var f in Directory.EnumerateFiles(Dir, "lauf_*.json"))
        {
            try { var s = Session.FromJson(File.ReadAllText(f)); if (s != null && s.Results.Count > 0) list.Add((f, s)); }
            catch { /* skip broken files */ }
        }
        return list.OrderByDescending(x => x.Item2.StartedAt).ToList();
    }

    public sealed record CompareRow(string Test, string Label, string Unit, string Format, double? A, double? B, bool HigherIsBetter, string? Blocker = null)
    {
        /// <summary>Null when not comparable (Blocker set, e.g. different render resolution).</summary>
        public double? DiffPct => Blocker == null && A is { } a && B is { } b && a != 0 ? (b - a) / Math.Abs(a) * 100.0 : null;
        /// <summary>+1 better, -1 worse, 0 = below 1 % (measurement noise) or unknown.</summary>
        public int Trend => DiffPct is not { } d || Math.Abs(d) < 1.0 ? 0 : (d > 0) == HigherIsBetter ? 1 : -1;
    }

    /// <summary>Metrics that depend on the render resolution / VSync.</summary>
    public static bool ResolutionDependent(string key) => key.StartsWith("gpu.3d") || key.StartsWith("ft.");

    /// <summary>Render conditions of a result. Runs before this was recorded always rendered at 1920×1080 without VSync (GPU) – stated as such.</summary>
    public static string? Conditions(BenchResult? r)
    {
        if (r == null || r.TestId is not ("gpu" or "frametime")) return null;
        string res = r.Info.GetValueOrDefault("resolution", "1920×1080");
        string vs = r.Info.GetValueOrDefault("vsync", r.Notes.Any(n => n.Contains("VSync an")) ? "an" : "aus");
        return $"{res}, VSync {vs}";
    }

    /// <summary>Warning text if two results of the same test were measured under different render conditions.</summary>
    public static string? Incomparable(BenchResult? ra, BenchResult? rb)
    {
        var ca = Conditions(ra); var cb = Conditions(rb);
        return ca != null && cb != null && ca != cb ? $"Nicht direkt vergleichbar: A {ca} · B {cb}" : null;
    }

    public static List<CompareRow> Compare(Session a, Session b)
    {
        var rows = new List<CompareRow>();
        var testIds = a.Results.Keys.Union(b.Results.Keys).ToList();
        foreach (var id in testIds)
        {
            a.Results.TryGetValue(id, out var ra);
            b.Results.TryGetValue(id, out var rb);
            var title = (rb ?? ra)!.Title;
            string? blocker = Incomparable(ra, rb);
            var keys = (ra?.Metrics ?? new()).Select(m => m.Key)
                .Union((rb?.Metrics ?? new()).Select(m => m.Key));
            foreach (var k in keys)
            {
                var ma = ra?.Metrics.FirstOrDefault(m => m.Key == k);
                var mb = rb?.Metrics.FirstOrDefault(m => m.Key == k);
                var m = mb ?? ma!;
                rows.Add(new CompareRow(title, m.Group != null && m.Group != title ? $"{m.Group} · {m.Label}" : m.Label, m.Unit, m.Format, ma?.Value, mb?.Value, m.HigherIsBetter,
                    ResolutionDependent(k) ? blocker : null));
            }
        }
        return rows;
    }
}

/// <summary>Human-readable labels for BenchResult.Info entries (test conditions).</summary>
public static class InfoLabels
{
    private static readonly (string Key, string Label, string Suffix)[] Map =
    {
        ("resolution", "Renderauflösung", ""), ("resolution_source", "Auflösungswahl", ""), ("monitor", "Hauptmonitor", ""),
        ("refresh_hz", "Bildwiederholrate", " Hz"), ("vsync", "VSync", ""), ("gpu", "Grafikkarte", ""),
        ("interface", "Schnittstelle", ""), ("interface_detail", "Verbindung", ""), ("drive_model", "Laufwerk", ""),
        ("cores", "Physische Kerne", ""), ("threads", "Threads", ""),
        ("ram_mts", "Eingestellter RAM-Takt", " MT/s"), ("ram_channels", "Speicherkanäle", ""), ("ram_label_mts", "Takt laut Modulbezeichnung", " MT/s"),
    };

    public static IEnumerable<(string Label, string Value)> Rows(Dictionary<string, string> info)
    {
        foreach (var (k, l, suf) in Map)
            if (info.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) yield return (l, v + suf);
    }
}