using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lagzero.Core;

/// <summary>
/// All thresholds and reference values of the evaluation. Stored as "bewertung.json" next to the exe so they can be
/// checked and changed. Values are interface/manufacturer limits – never results of other PCs.
/// </summary>
public sealed class Rules
{
    public string Hinweis { get; set; } =
        "Grundlagen und Schwellenwerte der LAGZERO-Auswertung. Änderbar; nach dem Speichern die Übersicht neu öffnen. " +
        "Prozent-Bewertungen: gemessener Wert ÷ theoretischer/praktischer Höchstwert der Klasse.";

    /// <summary>File format version; older files are extended automatically (user values are kept).</summary>
    public int Version { get; set; }   // 0 when missing in an old file → migrated on load
    public const int CurrentVersion = 4;

    public PercentScale Einordnung { get; set; } = new();
    public Dictionary<string, string> Ampel { get; set; } = new()
    {
        ["sehr gut"] = "gruen", ["gut"] = "gruen", ["ausreichend"] = "gelb", ["schwach"] = "rot",
    };

    public DriveRules Laufwerke { get; set; } = new();
    public RamRules Ram { get; set; } = new();
    public GpuRules Gpu { get; set; } = new();
    public FrametimeRules Frametime { get; set; } = new();
    public ControllerRules Controller { get; set; } = new();
    public CpuRules Cpu { get; set; } = new();
    public InternetRules Internet { get; set; } = new();

    public sealed class InternetRules
    {
        public string Grundlage { get; set; } =
            "Bufferbloat-Note aus dem Anstieg des Pings unter Last gegenüber dem Ruhezustand (Median, jeweils der schlechtere Wert von " +
            "Download- und Upload-Last). Gemessen mit M-Lab NDT7, Ping über eine eigene kleine Verbindung zum Messserver. " +
            "Download und Upload werden nicht bewertet: die gebuchte Geschwindigkeit ist dem Programm nicht bekannt.";
        public double APlusUnterMs { get; set; } = 5;
        public double AUnterMs { get; set; } = 30;
        public double BUnterMs { get; set; } = 60;
        public double CUnterMs { get; set; } = 200;
        public double DUnterMs { get; set; } = 400;
        public string Quelle { get; set; } = "M-Lab NDT7 (measurementlab.net), Protokoll: github.com/m-lab/ndt-server/blob/main/spec/ndt7-protocol.md";
    }

    public sealed class PercentScale
    {
        public double SehrGut { get; set; } = 90;
        public double Gut { get; set; } = 75;
        public double Ausreichend { get; set; } = 50;
    }

    public sealed class DriveClass
    {
        public string Name { get; set; } = "";
        public double MaxMbS { get; set; }
        public string Grundlage { get; set; } = "";
    }

    public sealed class DriveRules
    {
        public string Grundlage { get; set; } =
            "Sequenzielles Lesen/Schreiben im Verhältnis zur praktisch erreichbaren Datenrate der erkannten Schnittstelle (nicht des Geräts). " +
            "NVMe-Werte gelten für 4 PCIe-Lanes und werden bei weniger Lanes anteilig verringert.";
        public Dictionary<string, DriveClass> Klassen { get; set; } = new()
        {
            ["usb1"] = new() { Name = "USB 1.1", MaxMbS = 1.2, Grundlage = "12 Mbit/s brutto, praktisch ≈ 1,2 MB/s" },
            ["usb2"] = new() { Name = "USB 2.0", MaxMbS = 42, Grundlage = "480 Mbit/s brutto, praktisch ≈ 40–42 MB/s" },
            ["usb3_5"] = new() { Name = "USB 3.x 5 Gbit/s", MaxMbS = 450, Grundlage = "5 Gbit/s brutto (8b/10b), praktisch ≈ 450 MB/s" },
            ["usb3_10"] = new() { Name = "USB 3.x 10 Gbit/s", MaxMbS = 1050, Grundlage = "10 Gbit/s brutto (128b/132b), praktisch ≈ 1.050 MB/s" },
            ["hdd"] = new() { Name = "HDD (Festplatte)", MaxMbS = 250, Grundlage = "schnelle 7.200-U/min-Festplatten erreichen außen ≈ 250 MB/s (mechanische Grenze)" },
            ["sata"] = new() { Name = "SATA-SSD", MaxMbS = 560, Grundlage = "SATA 6 Gbit/s, praktisch ≈ 560 MB/s" },
            ["nvme_gen3"] = new() { Name = "NVMe PCIe Gen3", MaxMbS = 3500, Grundlage = "PCIe 3.0 x4 = 3,94 GB/s, praktisch ≈ 3.500 MB/s" },
            ["nvme_gen4"] = new() { Name = "NVMe PCIe Gen4", MaxMbS = 7400, Grundlage = "PCIe 4.0 x4 = 7,88 GB/s, praktisch ≈ 7.400 MB/s" },
            ["nvme_gen5"] = new() { Name = "NVMe PCIe Gen5", MaxMbS = 14500, Grundlage = "PCIe 5.0 x4 = 15,75 GB/s, praktisch ≈ 14.500 MB/s" },
        };
    }

    public sealed class RamRules
    {
        public string Grundlage { get; set; } =
            "Theoretische Bandbreite = Kanäle × eingestellter Takt (MT/s) × 8 Byte. Kopieren liest und schreibt jedes Byte, " +
            "deshalb zählt dort die doppelte Datenmenge.";
        public double KopierenFaktor { get; set; } = 2;
    }

    public sealed class GpuCard
    {
        /// <summary>Model as in the driver name without "NVIDIA"/"GeForce", e.g. "GTX 1080 Ti", "RTX 2070 SUPER", "TITAN RTX".</summary>
        public string Name { get; set; } = "";
        public double TflopsFp32 { get; set; }
        public int Shader { get; set; }
        public int BoostTaktLautHerstellerMhz { get; set; }
        /// <summary>Optional VRAM range to tell variants apart (e.g. GTX 1060 3 GB / 6 GB, RTX 2060 6 GB / 12 GB).</summary>
        public double? VramMinGb { get; set; }
        public double? VramMaxGb { get; set; }
        public string Quelle { get; set; } = "";
        public string? Hinweis { get; set; }
    }

    public sealed class GpuRules
    {
        public string Grundlage { get; set; } =
            "Gemessene FP32-Rechenleistung (Compute-Test) ÷ theoretische Spitzenleistung laut Hersteller = Shader × 2 × offizieller Boost-Takt. " +
            "Werte leicht über 100 % sind normal: Die Karte taktet im Betrieb oft höher als der Herstellerwert (GPU Boost). " +
            "Erkannt wird der Kartenname Wort für Wort (längste Namen zuerst), Varianten zusätzlich über den Grafikspeicher. " +
            "Laptop-Varianten (Laptop, Mobile, Max-Q oder Notebook-Gehäuse) sind nicht bewertbar, weil ihre Leistung vom Gerät abhängt. " +
            "Nicht aufgeführte Karten sind nicht bewertbar – Einträge können hier ergänzt werden.";

        public List<GpuCard> Karten { get; set; } = DefaultCards();

        private static GpuCard C(string name, int shader, int boost, string quelle, double? vmin = null, double? vmax = null, string? hinweis = null) => new()
        {
            Name = name, Shader = shader, BoostTaktLautHerstellerMhz = boost,
            TflopsFp32 = Math.Round(shader * 2.0 * boost / 1e6, 2), Quelle = quelle, VramMinGb = vmin, VramMaxGb = vmax, Hinweis = hinweis,
        };

        private const string Tpu = "TechPowerUp GPU Database (Referenz-Boost)";
        private const string TpuCalc = "TechPowerUp: Shader und Referenz-Boost; TFLOPS = Shader × 2 × Boost berechnet";
        private const string Nv = "NVIDIA Produktseite / Datenblatt";

        public static List<GpuCard> DefaultCards() => new()
        {
            // RTX 50
            C("RTX 5090", 21760, 2407, Nv), C("RTX 5080", 10752, 2617, Nv), C("RTX 5070 Ti", 8960, 2452, Nv), C("RTX 5070", 6144, 2512, Nv),
            // RTX 40
            C("RTX 4090", 16384, 2520, Nv), C("RTX 4080 SUPER", 10240, 2550, Nv), C("RTX 4080", 9728, 2505, Nv),
            C("RTX 4070 Ti SUPER", 8448, 2610, Nv), C("RTX 4070 Ti", 7680, 2610, Nv), C("RTX 4070 SUPER", 7168, 2475, Nv),
            C("RTX 4070", 5888, 2475, Nv), C("RTX 4060 Ti", 4352, 2535, Nv), C("RTX 4060", 3072, 2460, Nv),
            // RTX 30
            C("RTX 3090 Ti", 10752, 1860, Nv), C("RTX 3090", 10496, 1695, Nv), C("RTX 3080 Ti", 10240, 1665, Nv),
            C("RTX 3080", 8704, 1710, Nv, null, 11, "nur 10-GB-Version (die 12-GB-Version hat 8.960 Shader)"),
            C("RTX 3070 Ti", 6144, 1770, Nv), C("RTX 3070", 5888, 1725, Nv), C("RTX 3060 Ti", 4864, 1665, Nv), C("RTX 3060", 3584, 1777, Nv),
            // RTX 20 / TITAN RTX (Turing)
            C("TITAN RTX", 4608, 1770, "NVIDIA TITAN-RTX-Produktseite und Datenblatt (16,3 TFLOPS)"),
            C("RTX 2080 Ti", 4352, 1545, Tpu + " – Founders Edition: 1.635 MHz"),
            C("RTX 2080 SUPER", 3072, 1815, Tpu),
            C("RTX 2080", 2944, 1710, TpuCalc + " – Founders Edition: 1.800 MHz"),
            C("RTX 2070 SUPER", 2560, 1770, Tpu),
            C("RTX 2070", 2304, 1620, Tpu + " – Founders Edition: 1.710 MHz"),
            C("RTX 2060 SUPER", 2176, 1650, Tpu),
            C("RTX 2060", 1920, 1680, TpuCalc, null, 7, "nur 6-GB-Version (die 12-GB-Version hat 2.176 Shader)"),
            // GTX 10 (Pascal)
            C("GTX 1080 Ti", 3584, 1582, Tpu),
            C("GTX 1080", 2560, 1733, TpuCalc),
            C("GTX 1070 Ti", 2432, 1683, Tpu),
            C("GTX 1070", 1920, 1683, TpuCalc),
            C("GTX 1060", 1280, 1708, Tpu + " (6-GB-Version)", 5.5, 7, "6-GB-Version, erkannt über den Grafikspeicher"),
            C("GTX 1060", 1152, 1708, Tpu + " (3-GB-Version)", null, 3.5, "3-GB-Version, erkannt über den Grafikspeicher"),
            C("GTX 1050 Ti", 768, 1392, Tpu),
            C("GTX 1050", 640, 1455, TpuCalc + " (2-GB-Version)", null, 2.5, "2-GB-Version (die 3-GB-Version hat 768 Shader)"),
            C("GT 1030", 384, 1468, Tpu + " (GDDR5-Version)", null, null, "Die DDR4-Version heißt gleich, taktet aber niedriger (1.379 MHz Boost) und ist nicht unterscheidbar – dort fällt der Prozentwert niedriger aus."),
        };

        private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase) { "NVIDIA", "GEFORCE", "(R)", "(TM)", "GRAPHICS" };
        private static readonly string[] LaptopTokens = { "LAPTOP", "MOBILE", "MAX-Q", "MAXQ", "NOTEBOOK" };

        public static List<string> Tokens(string name) =>
            System.Text.RegularExpressions.Regex.Split(name.ToUpperInvariant().Replace("™", " ").Replace("®", " "), @"\s+")
                .Where(t => t.Length > 0 && !Noise.Contains(t) && !System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+GB$"))
                .ToList();

        /// <summary>Finds the card entry for a driver name. Returns null with a reason when the card is not assessable.</summary>
        public (GpuCard? Card, string? Reason) Match(string adapterName, double? vramGb, bool portable)
        {
            var tokens = Tokens(adapterName);
            string upper = adapterName.ToUpperInvariant();
            if (LaptopTokens.Any(t => tokens.Contains(t)) || upper.Contains("MAX-Q") || upper.Contains("MAX Q"))
                return (null, $"„{adapterName}“ ist eine Laptop-Variante (Laptop/Mobile/Max-Q) – nicht bewertbar, weil Takt und Leistungsgrenze vom Gerät abhängen.");
            if (portable)
                return (null, $"„{adapterName}“ läuft in einem Notebook-Gehäuse – Laptop-Grafik, nicht bewertbar (Leistung hängt vom Gerät ab).");
            // strip "WITH ... DESIGN" style suffixes
            int with = tokens.IndexOf("WITH");
            if (with >= 0) tokens = tokens.Take(with).ToList();
            var candidates = Karten.Where(k => Tokens(k.Name).SequenceEqual(tokens))
                                   .OrderByDescending(k => k.Name.Length).ToList();
            if (candidates.Count == 0)
                return (null, $"„{adapterName}“ ist nicht in bewertung.json eingetragen – ohne Herstellerangabe nicht bewertbar (Eintrag kann ergänzt werden).");
            var fitting = candidates.Where(k => (k.VramMinGb == null || vramGb >= k.VramMinGb) && (k.VramMaxGb == null || vramGb <= k.VramMaxGb)).ToList();
            if (candidates.Any(k => k.VramMinGb != null || k.VramMaxGb != null) && vramGb == null)
                return (null, $"„{adapterName}“ gibt es in mehreren Varianten, der Grafikspeicher ist unbekannt – nicht bewertbar.");
            if (fitting.Count != 1)
                return (null, $"„{adapterName}“ mit {vramGb:0.#} GB passt zu keiner eingetragenen Variante eindeutig – nicht bewertbar.");
            return (fitting[0], null);
        }
    }
    public sealed class FrametimeRules
    {
        public string Grundlage { get; set; } =
            "1 % Low (FPS der langsamsten 1 % aller Bilder) im Verhältnis zur echten Bildwiederholrate des Hauptmonitors. " +
            "„Flüssig bis X Hz“ = höchste Monitorstufe, die das 1 % Low noch erreicht. Gilt für die Testszene, nicht für jedes Spiel.";
        public List<int> MonitorStufenHz { get; set; } = new() { 60, 120, 144, 240, 360 };
        /// <summary>1 % Low ÷ Bildwiederholrate.</summary>
        public PercentScale AnteilBildwiederholrateProzent { get; set; } = new() { SehrGut = 95, Gut = 75, Ausreichend = 50 };
        public double RucklerProzentGelb { get; set; } = 0.1;
        public double RucklerProzentRot { get; set; } = 1.0;
    }

    public sealed class ControllerRules
    {
        public string Grundlage { get; set; } =
            "Polling-Rate (Median) eingeordnet in USB-Polling-Klassen; Gleichmäßigkeit = Jitter (Standardabweichung der Paketabstände) absolut in ms; " +
            "Drift = größte Stick-Auslenkung in Ruhe in % des Wegs.";
        public List<int> KlassenHz { get; set; } = new() { 125, 250, 500, 1000, 2000, 4000, 8000 };
        public double SehrGutAbHz { get; set; } = 1000;
        public double GutAbHz { get; set; } = 500;
        public double AusreichendAbHz { get; set; } = 250;
        /// <summary>Absolute jitter (standard deviation of packet intervals) in ms.</summary>
        public double JitterSehrGutBisMs { get; set; } = 0.1;
        public double JitterGutBisMs { get; set; } = 0.25;
        public double JitterAusreichendBisMs { get; set; } = 0.5;
        public double DriftKeinBisProzent { get; set; } = 2;
        public double DriftLeichtBisProzent { get; set; } = 5;
        public double DriftDeutlichBisProzent { get; set; } = 10;
    }

    public sealed class CpuRules
    {
        public string Grundlage { get; set; } =
            "Ohne Vergleichsdatenbank ist kein Vergleich mit anderen PCs möglich. Bewertet wird: Kernauslastung = (Mehrkern ÷ Einzelkern) ÷ Anzahl " +
            "physischer Kerne (Ganzzahl und Fließkomma), Stabilität = größte Abweichung der letzten Läufe dieses Prozessors vom Mittelwert, " +
            "und die Veränderung gegenüber dem vorherigen Lauf aus dem Verlauf.";
        public PercentScale KernauslastungProzent { get; set; } = new() { SehrGut = 85, Gut = 70, Ausreichend = 50 };
        public double StabilBisProzent { get; set; } = 3;
        public double LeichtSchwankendBisProzent { get; set; } = 7;
        public int StabilitaetLaeufe { get; set; } = 5;
        public double VeraenderungHinweisAbProzent { get; set; } = 5;
    }

    // ---------------------------------------------------------------------------------------------

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FilePath
    {
        get
        {
            string p = Path.Combine(AppContext.BaseDirectory, "bewertung.json");
            try
            {
                if (File.Exists(p)) return p;
                File.WriteAllText(p, JsonSerializer.Serialize(new Rules { Version = CurrentVersion }, Json));
                return p;
            }
            catch { return Path.Combine(AppInfo.DataDir(""), "bewertung.json"); }
        }
    }

    /// <summary>Loads bewertung.json (creates it with the defaults if missing). On a broken file the defaults are used and Error is set.</summary>
    /// <summary>Adds new default cards and fills new fields; existing values (e.g. own TFLOPS) are kept.</summary>
    private void Migrate()
    {
        var defaults = GpuRules.DefaultCards();
        foreach (var d in defaults)
        {
            var existing = Gpu.Karten.Where(k => k.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var same = existing.FirstOrDefault(k => k.Shader == d.Shader || (k.Shader == 0 && existing.Count == 1 && defaults.Count(x => x.Name == d.Name) == 1));
            if (same == null) { Gpu.Karten.Add(d); continue; }
            if (same.Shader == 0) same.Shader = d.Shader;
            if (same.BoostTaktLautHerstellerMhz == 0) same.BoostTaktLautHerstellerMhz = d.BoostTaktLautHerstellerMhz;
            same.VramMinGb ??= d.VramMinGb; same.VramMaxGb ??= d.VramMaxGb; same.Hinweis ??= d.Hinweis;
            if (string.IsNullOrWhiteSpace(same.Quelle) || same.Quelle.StartsWith("NVIDIA:")) same.Quelle = d.Quelle;
        }
        Gpu.Grundlage = new GpuRules().Grundlage;
        Controller.Grundlage = new ControllerRules().Grundlage;   // v3: jitter is rated in absolute ms
        Version = CurrentVersion;
    }

    public static Rules Load(out string? error)
    {
        error = null;
        string p = FilePath;
        try
        {
            if (!File.Exists(p)) File.WriteAllText(p, JsonSerializer.Serialize(new Rules { Version = CurrentVersion }, Json));
            var r = JsonSerializer.Deserialize<Rules>(File.ReadAllText(p), Json) ?? new Rules();
            if (r.Version < CurrentVersion) { r.Migrate(); try { File.WriteAllText(p, JsonSerializer.Serialize(r, Json)); } catch { } }
            return r;
        }
        catch (Exception ex)
        {
            error = $"bewertung.json konnte nicht gelesen werden ({ex.Message}) – Standardwerte verwendet.";
            return new Rules();
        }
    }
}
