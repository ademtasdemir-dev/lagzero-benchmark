using System.Globalization;
using System.Text.RegularExpressions;

namespace Lagzero.Core;

public enum Light { Green, Yellow, Red, Gray }

/// <summary>One measured value with a plain-language explanation.</summary>
public sealed record EvalLine(string Label, string Value, string Meaning);

public sealed class SectionEval
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public Light Light { get; set; } = Light.Gray;
    public string Rating { get; set; } = "nicht bewertbar";
    /// <summary>What the rating is based on – shown right next to it.</summary>
    public List<string> Basis { get; set; } = new();
    public List<EvalLine> Lines { get; set; } = new();
    public List<string> Tips { get; set; } = new();
    /// <summary>One sentence for the summary, in plain German without technical terms.</summary>
    public string? Fazit { get; set; }
}

public sealed class Evaluation
{
    public List<string> Fazit { get; set; } = new();
    public List<SectionEval> Sections { get; set; } = new();
    public string RulesFile { get; set; } = "";
    public string? RulesError { get; set; }
}

/// <summary>Turns measured values into a traffic light, a rating and plain-language explanations – only on documented bases (bewertung.json).</summary>
public static class Evaluator
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static string F(double v, string fmt) => v.ToString(fmt, De);
    private static string P(double v) => v.ToString("0", De) + " %";

    public static Evaluation Evaluate(Session s, IEnumerable<Session> history)
    {
        var rules = Rules.Load(out var err);
        var ev = new Evaluation { RulesFile = Rules.FilePath, RulesError = err };
        var hist = history.Where(h => h.Id != s.Id).ToList();
        SectionEval Safe(string id, string title, Func<SectionEval> f)
        {
            try { return f(); }
            catch (Exception ex) { return new SectionEval { Id = id, Title = title, Basis = { $"Bewertung nicht möglich ({ex.Message}) – nicht bewertbar." } }; }
        }
        ev.Sections.Add(Safe("cpu", "Prozessor (CPU)", () => Cpu(s, hist, rules)));
        ev.Sections.Add(Safe("ram", "Arbeitsspeicher (RAM)", () => Ram(s, rules)));
        ev.Sections.Add(Safe("gpu", "Grafikkarte (GPU)", () => Gpu(s, rules)));
        ev.Sections.Add(Safe("frametime", "Bildausgabe (Frametime)", () => Frametime(s, rules)));
        ev.Sections.Add(Safe("drive", "Laufwerke & USB", () => Drives(s, rules)));
        ev.Sections.Add(Safe("controller", "Controller", () => Controller(s, rules)));
        if (s.Results.ContainsKey("internet")) ev.Sections.Add(Safe("internet", "Internet", () => Internet(s, rules)));
        BuildFazit(ev);
        return ev;
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static double? M(BenchResult? r, string key) => r?.Metrics.FirstOrDefault(m => m.Key == key)?.Value;

    private static double? I(BenchResult? r, string key) =>
        r != null && r.Info.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static (string Rating, Light Light) Rate(double value, Rules.PercentScale sc, Rules rules) =>
        Label(value >= sc.SehrGut ? "sehr gut" : value >= sc.Gut ? "gut" : value >= sc.Ausreichend ? "ausreichend" : "schwach", rules);

    private static (string, Light) Label(string rating, Rules rules) =>
        (rating, rules.Ampel.TryGetValue(rating, out var c) ? c switch { "gruen" => Light.Green, "gelb" => Light.Yellow, "rot" => Light.Red, _ => Light.Gray } : Light.Gray);

    private static string ScaleText(Rules.PercentScale sc, string unit = "%") =>
        $"Grenzen (bewertung.json): sehr gut ≥ {F(sc.SehrGut, "0.##")} {unit}, gut ≥ {F(sc.Gut, "0.##")} {unit}, ausreichend ≥ {F(sc.Ausreichend, "0.##")} {unit}, darunter schwach.";


    private static SectionEval NotRun(string id, string title, string what) => new()
    {
        Id = id, Title = title, Light = Light.Gray, Rating = "nicht bewertbar",
        Basis = { $"{what} wurde in diesem Lauf nicht ausgeführt – keine Grundlage für eine Bewertung." },
    };

    // ---- CPU --------------------------------------------------------------------------------

    private static SectionEval Cpu(Session s, List<Session> hist, Rules rules)
    {
        s.Results.TryGetValue("cpu", out var r);
        if (r == null) return NotRun("cpu", "Prozessor (CPU)", "Der CPU-Test");
        var cr = rules.Cpu;
        var e = new SectionEval { Id = "cpu", Title = "Prozessor (CPU)" };
        e.Basis.Add(cr.Grundlage);

        double? cores = I(r, "cores");
        double? si = M(r, "cpu.int.scaling"), sf = M(r, "cpu.float.scaling");
        (string, Light) scaleRate = ("nicht bewertbar", Light.Gray);
        if (cores is > 0 && si is { } a && sf is { } b)
        {
            double pi = a / cores.Value * 100, pf = b / cores.Value * 100, worst = Math.Min(pi, pf);
            scaleRate = Rate(worst, cr.KernauslastungProzent, rules);
            e.Basis.Add($"Kernauslastung: Ganzzahl {F(a, "0.0")}× ÷ {cores} Kerne = {P(pi)}, Fließkomma {F(b, "0.0")}× ÷ {cores} Kerne = {P(pf)}. " +
                        ScaleText(cr.KernauslastungProzent));
            e.Lines.Add(new("Kernauslastung", P(worst),
                worst >= 100
                    ? "Alle Kerne arbeiten voll mit – über 100 % kommt durch Hyper-Threading (zwei Aufgaben pro Kern) zustande."
                    : worst >= cr.KernauslastungProzent.Gut
                        ? "Die Kerne arbeiten gut zusammen; Programme, die viele Kerne nutzen, profitieren fast voll davon."
                        : "Die Kerne arbeiten nicht voll zusammen – mögliche Ursachen: Hintergrundlast, Stromsparmodus oder Temperaturgrenze."));
            if (worst < cr.KernauslastungProzent.Gut)
                e.Tips.Add("Energieoptionen auf „Höchstleistung“ stellen, Hintergrundprogramme schließen und die CPU-Temperatur prüfen, dann erneut testen.");
        }
        else e.Basis.Add("Kernauslastung: nicht bewertbar (Kernzahl oder Mehrkern-Ergebnis fehlt).");

        if (M(r, "cpu.int.single") is { } s1)
            e.Lines.Add(new("Einzelkern-Leistung", F(s1, "N0") + " Punkte",
                "So schnell arbeitet ein einzelner Kern. Wichtig für Spiele und die meisten Alltagsprogramme. Nur mit eigenen früheren Läufen vergleichbar."));
        if (M(r, "cpu.int.multi") is { } m1)
            e.Lines.Add(new("Mehrkern-Leistung", F(m1, "N0") + " Punkte",
                "Alle Kerne zusammen – wichtig für Videoschnitt, Entpacken, Streaming und Programmieren."));
        if (M(r, "cpu.simd.single") is { } v1)
            e.Lines.Add(new("Spezialbefehle (SIMD)", F(v1, "N0") + " Punkte",
                "Rechenbefehle, die viele Zahlen auf einmal verarbeiten (Video, Bild, Physik)." +
                (r.Info.ContainsKey("e_cores") ? " Mit allen Kernen steigt dieser Wert weniger stark, weil die Effizienzkerne dafür kleinere Recheneinheiten haben." : "")));

        // stability across runs of the same processor
        string cpuName = r.Info.TryGetValue("cpu", out var cn) ? cn : "";
        string method = r.Info.GetValueOrDefault("method", "");
        var prior = hist.Where(h => h.Results.TryGetValue("cpu", out var hr) && !hr.Cancelled &&
                                    hr.Info.GetValueOrDefault("method", "") == method &&          // same measuring method only
                                    (cpuName.Length == 0 || !hr.Info.TryGetValue("cpu", out var hn) || hn == cpuName))
                        .OrderByDescending(h => h.StartedAt).Take(cr.StabilitaetLaeufe - 1).ToList();
        Light stab = Light.Gray;
        if (prior.Count >= 1 && M(r, "cpu.int.single") is { } cur && M(r, "cpu.int.multi") is { } curM)
        {
            double Dev(IEnumerable<double> xs) { var l = xs.ToList(); double mean = l.Average(); return l.Max(x => Math.Abs(x - mean) / mean * 100); }
            var singles = prior.Select(h => M(h.Results["cpu"], "cpu.int.single")).OfType<double>().Append(cur);
            var multis = prior.Select(h => M(h.Results["cpu"], "cpu.int.multi")).OfType<double>().Append(curM);
            double dev = Math.Max(Dev(singles), Dev(multis));
            string word = dev <= cr.StabilBisProzent ? "stabil" : dev <= cr.LeichtSchwankendBisProzent ? "leicht schwankend" : "schwankend";
            stab = dev <= cr.StabilBisProzent ? Light.Green : dev <= cr.LeichtSchwankendBisProzent ? Light.Yellow : Light.Red;
            e.Basis.Add($"Stabilität: größte Abweichung vom Mittelwert über {prior.Count + 1} Läufe = {F(dev, "0.0")} % " +
                        $"(stabil ≤ {F(cr.StabilBisProzent, "0.#")} %, leicht schwankend ≤ {F(cr.LeichtSchwankendBisProzent, "0.#")} %).");
            e.Lines.Add(new("Stabilität", word, dev <= cr.StabilBisProzent
                ? "Bei Wiederholung kommen fast identische Ergebnisse – der Prozessor taktet zuverlässig."
                : "Die Ergebnisse schwanken zwischen den Läufen – oft durch Hintergrundprogramme, Wärme oder Energiesparen."));
            if (stab != Light.Green) e.Tips.Add("Für verlässliche Werte: vor dem Test Spiele, Browser und Updates schließen und den PC nach dem Start 2–3 Minuten ruhen lassen.");

            var last = prior[0].Results["cpu"];
            if (M(last, "cpu.int.single") is { } ls && M(last, "cpu.int.multi") is { } lm)
            {
                double ds = (cur - ls) / ls * 100, dm = (curM - lm) / lm * 100;
                e.Lines.Add(new("Seit dem letzten Lauf", $"{F(ds, "+0.0;-0.0")} % / {F(dm, "+0.0;-0.0")} %",
                    $"Einzelkern / Mehrkern im Vergleich zum Lauf vom {prior[0].StartedAt:dd.MM.yyyy HH:mm}." +
                    (Math.Max(Math.Abs(ds), Math.Abs(dm)) >= cr.VeraenderungHinweisAbProzent
                        ? " Deutliche Veränderung – wurde etwas am PC geändert (BIOS, Treiber, Kühlung, Energieplan)?"
                        : " Praktisch unverändert.")));
            }
        }
        else
        {
            e.Basis.Add("Stabilität: nicht bewertbar – erst ab dem zweiten CPU-Lauf dieses Prozessors mit derselben Messmethode im Verlauf.");
            e.Lines.Add(new("Stabilität", "nicht bewertbar", "Wird ab dem zweiten Testlauf aus dem Verlauf berechnet."));
        }

        (e.Rating, e.Light) = scaleRate;
        if (stab is Light.Yellow or Light.Red && e.Light == Light.Green) e.Light = Light.Yellow;
        e.Fazit = scaleRate.Item2 == Light.Gray ? null :
            $"Der Prozessor nutzt seine Kerne {e.Rating} aus" +
            (stab == Light.Green ? " und liefert bei jedem Durchlauf gleiche Ergebnisse." : stab == Light.Gray ? "." : ", die Ergebnisse schwanken aber zwischen den Läufen.");
        return e;
    }

    // ---- RAM --------------------------------------------------------------------------------

    private static SectionEval Ram(Session s, Rules rules)
    {
        s.Results.TryGetValue("ram", out var r);
        if (r == null) return NotRun("ram", "Arbeitsspeicher (RAM)", "Der RAM-Test");
        var e = new SectionEval { Id = "ram", Title = "Arbeitsspeicher (RAM)" };
        e.Basis.Add(rules.Ram.Grundlage);
        double? mts = I(r, "ram_mts"), ch = I(r, "ram_channels");
        double? read = M(r, "ram.read"), write = M(r, "ram.write"), copy = M(r, "ram.copy");

        if (mts is > 0 && ch is > 0)
        {
            double theo = ch.Value * mts.Value * 8; // MB/s (10^6)
            e.Basis.Add($"Theoretisch: {ch} Kanal/Kanäle × {F(mts.Value, "0")} MT/s × 8 Byte = {F(theo, "N0")} MB/s " +
                        $"(Kanäle {(r.Info.TryGetValue("ram_channels_basis", out var cb) ? cb : "")}). " + ScaleText(rules.Einordnung));
            double pr = read is { } rd ? rd / theo * 100 : 0, pw = write is { } wr ? wr / theo * 100 : 0;
            (e.Rating, e.Light) = Rate(Math.Min(pr, pw), rules.Einordnung, rules);
            if (read is { } rv) e.Lines.Add(new("Lesen", $"{F(rv, "N0")} MB/s = {P(pr)}",
                "Wie schnell der Prozessor Daten aus dem Arbeitsspeicher holt – z. B. beim Laden von Spielwelten."));
            if (write is { } wv) e.Lines.Add(new("Schreiben", $"{F(wv, "N0")} MB/s = {P(pw)}",
                "Wie schnell Daten im Arbeitsspeicher abgelegt werden."));
            if (copy is { } cv)
            {
                double pc = cv * rules.Ram.KopierenFaktor / theo * 100;
                e.Lines.Add(new("Kopieren", $"{F(cv, "N0")} MB/s = {P(pc)}",
                    $"Daten innerhalb des Speichers umlagern. Jedes Byte wird gelesen und geschrieben, daher zählt die {F(rules.Ram.KopierenFaktor, "0")}-fache Menge."));
            }
            if (ch == 1) e.Tips.Add("Nur ein Speicherkanal belegt: Ein zweites, gleiches Modul im passenden Steckplatz (Handbuch) verdoppelt die mögliche Geschwindigkeit.");
        }
        else
        {
            e.Basis.Add("Theoretische Bandbreite nicht berechenbar: " +
                        (mts is not > 0 ? "eingestellter Takt unbekannt" : r.Info.GetValueOrDefault("ram_channels_basis", "Kanalzahl unbekannt")) + ".");
            if (read is { } rv) e.Lines.Add(new("Lesen", F(rv, "N0") + " MB/s", "Wie schnell der Prozessor Daten aus dem Arbeitsspeicher holt."));
        }
        if (M(r, "ram.latency") is { } lat)
            e.Lines.Add(new("Wartezeit (Latenz)", F(lat, "0.0") + " ns",
                "Wie lange es dauert, bis ein einzelner Wert aus dem Speicher ankommt – niedriger ist besser. Nicht bewertet: dafür gibt es keine theoretische Grundlage."));

        if (I(r, "ram_label_mts") is { } label && mts is { } set && set < label)
            e.Tips.Add($"Laut Modulbezeichnung ({r.Info.GetValueOrDefault("ram_partnumber", "")}) sind {F(label, "0")} MT/s vorgesehen, eingestellt sind {F(set, "0")} MT/s. " +
                       "Im BIOS das XMP/EXPO-Profil aktivieren – bei manuell getuntem RAM ignorieren.");

        e.Fazit = e.Light == Light.Gray ? null :
            $"Der Arbeitsspeicher arbeitet mit {P(Math.Min(read ?? 0, write ?? 0) / (I(r, "ram_channels")!.Value * I(r, "ram_mts")!.Value * 8) * 100)} seiner möglichen Geschwindigkeit bei der eingestellten Taktung" +
            (e.Tips.Any(t => t.Contains("XMP")) ? " – laut Etikett könnte er noch höher getaktet werden." : ".");
        return e;
    }

    // ---- GPU --------------------------------------------------------------------------------

    private static SectionEval Gpu(Session s, Rules rules)
    {
        s.Results.TryGetValue("gpu", out var r);
        if (r == null) return NotRun("gpu", "Grafikkarte (GPU)", "Der GPU-Test");
        var e = new SectionEval { Id = "gpu", Title = "Grafikkarte (GPU)" };
        e.Basis.Add(rules.Gpu.Grundlage);
        string name = r.Info.GetValueOrDefault("gpu", s.SystemInfo.FirstOrDefault(i => i.Section == "GPU")?.Label ?? "");
        double? vram = I(r, "vram_gb");
        bool portable = r.Info.GetValueOrDefault("portable") == "1";
        var (card, reason) = rules.Gpu.Match(name, vram, portable);
        double? tf = M(r, "gpu.compute.gflops");
        if (card != null && tf is { } t && card.TflopsFp32 > 0)
        {
            double pct = t / card.TflopsFp32 * 100;
            (e.Rating, e.Light) = Rate(pct, rules.Einordnung, rules);
            e.Basis.Add($"Erkannte Karte: {name}{(vram is { } vg ? $" ({F(vg, "0.#")} GB)" : "")} → Eintrag „{card.Name}“: {F(card.Shader, "N0")} Shader × 2 × " +
                        $"{F(card.BoostTaktLautHerstellerMhz, "N0")} MHz Boost-Takt laut Hersteller = {F(card.TflopsFp32, "0.00")} TFLOPS. Quelle: {card.Quelle}." +
                        (card.Hinweis != null ? $" Hinweis: {card.Hinweis}" : ""));
            e.Basis.Add($"Gemessen {F(t, "0.00")} TFLOPS = {P(pct)}. " + ScaleText(rules.Einordnung));
            e.Lines.Add(new("Rechenleistung", $"{F(t, "0.0")} TFLOPS = {P(pct)} der Herstellerangabe",
                pct > 100 ? "Etwas mehr als die Herstellerangabe – normal: Die Karte taktet im Betrieb oft höher als der offizielle Boost-Takt, solange Kühlung und Strom reichen."
                          : pct >= rules.Einordnung.Gut ? "Die Karte bringt ihre volle Rechenleistung."
                                                        : "Die Karte bleibt deutlich unter ihrer möglichen Leistung."));
            if (pct < rules.Einordnung.Gut)
                e.Tips.Add("Grafiktreiber aktualisieren, im Treiber den Energiemodus „Maximale Leistung“ wählen, Temperaturen und Stromanschlüsse der Karte prüfen.");
            e.Fazit = $"Die Grafikkarte erreicht {P(pct)} ihrer vom Hersteller angegebenen Rechenleistung" +
                      (pct >= rules.Einordnung.Gut ? " – sie arbeitet so schnell, wie sie soll." : " – hier geht Leistung verloren.");
        }
        else
        {
            e.Basis.Add(card == null ? reason ?? "Karte nicht erkannt – nicht bewertbar." : "Compute-Ergebnis fehlt – nicht bewertbar.");
            if (tf is { } t2) e.Lines.Add(new("Rechenleistung", F(t2, "0.0") + " TFLOPS", "Billionen Rechenschritte pro Sekunde – nicht bewertet, weil keine passende Herstellerangabe vorliegt."));
        }
        if (r.Info.TryGetValue("feature_level", out var fl))
            e.Basis.Add($"Test lief mit Direct3D 11, Feature-Level {fl}.");
        if (M(r, "gpu.3d.fps") is { } fps)
            e.Lines.Add(new("3D-Testszene", $"{F(fps, "N0")} Bilder/s bei {r.Info.GetValueOrDefault("resolution", "1920×1080")}",
                "So viele Bilder pro Sekunde schafft die Karte in unserer Testszene. Nicht bewertet – ohne Vergleichsdatenbank gibt es keinen Maßstab; nur mit eigenen Läufen gleicher Auflösung vergleichbar."));
        return e;
    }

    // ---- Frametime --------------------------------------------------------------------------

    private static SectionEval Frametime(Session s, Rules rules)
    {
        s.Results.TryGetValue("frametime", out var r);
        if (r == null) return NotRun("frametime", "Bildausgabe (Frametime)", "Der Frametime-Test");
        var fr = rules.Frametime;
        var e = new SectionEval { Id = "frametime", Title = "Bildausgabe (Frametime)" };
        e.Basis.Add(fr.Grundlage);
        double? hz = I(r, "refresh_hz"), low = M(r, "ft.low1"), fps = M(r, "ft.fps"), stPct = M(r, "ft.stutter.pct"), st = M(r, "ft.stutter");
        string cond = $"{r.Info.GetValueOrDefault("resolution", "?")}, VSync {r.Info.GetValueOrDefault("vsync", "?")}";

        if (low is { } l)
        {
            int smooth = fr.MonitorStufenHz.Where(x => x <= l * 1.0 + 0.5).DefaultIfEmpty(0).Max();
            string smoothText = smooth > 0 ? $"flüssig bis zu einem Monitor mit {smooth} Hz" : $"unter {fr.MonitorStufenHz.Min()} Hz – nicht durchgehend flüssig";
            e.Lines.Add(new("1 % Low", $"{F(l, "N0")} Bilder/s → {smoothText}",
                "Die langsamsten 1 % aller Bilder. Liegt dieser Wert über der Bildwiederholrate des Monitors, wirkt das Bild durchgehend flüssig."));
            if (hz is > 0)
            {
                double ratio = l / hz.Value * 100;
                (e.Rating, e.Light) = Rate(ratio, fr.AnteilBildwiederholrateProzent, rules);
                e.Basis.Add($"Dein Hauptmonitor: {F(hz.Value, "0")} Hz. 1 % Low {F(l, "N0")} ÷ {F(hz.Value, "0")} Hz = {P(ratio)} ({cond}). " +
                            ScaleText(fr.AnteilBildwiederholrateProzent));
                e.Fazit = (smooth > 0 ? $"Die Testszene läuft flüssig bis zu einem {smooth}-Hz-Monitor" : "Die Testszene läuft nicht durchgehend flüssig") +
                          $"; dein Monitor hat {F(hz.Value, "0")} Hz" +
                          (ratio >= fr.AnteilBildwiederholrateProzent.SehrGut ? " – also genug Reserve." : " – das reicht nicht immer für ein ganz ruhiges Bild.");
                if (ratio < fr.AnteilBildwiederholrateProzent.Gut)
                    e.Tips.Add("Grafikdetails oder Auflösung senken oder eine Bildratenbegrenzung knapp unter der Bildwiederholrate setzen, damit das Bild gleichmäßiger wird.");
            }
            else e.Basis.Add("Bildwiederholrate des Monitors unbekannt – Verhältnis nicht bewertbar.");
        }
        if (fps is { } f)
            e.Lines.Add(new("Bilder pro Sekunde (Durchschnitt)", F(f, "N0"), "Wie viele Bilder im Mittel pro Sekunde entstehen. Allein sagt das wenig über Ruckler aus."));
        if (stPct is { } sp)
        {
            Light sl = sp <= fr.RucklerProzentGelb ? Light.Green : sp <= fr.RucklerProzentRot ? Light.Yellow : Light.Red;
            e.Basis.Add($"Ruckler: {F(st ?? 0, "0")} Bilder = {F(sp, "0.###")} % (grün ≤ {F(fr.RucklerProzentGelb, "0.###")} %, gelb ≤ {F(fr.RucklerProzentRot, "0.###")} %).");
            e.Lines.Add(new("Ruckler", $"{F(st ?? 0, "0")} ({F(sp, "0.###")} %)",
                sp == 0 ? "Kein einziges Bild hat spürbar länger gedauert – keine sichtbaren Hänger."
                        : "So viele Bilder haben deutlich länger gedauert als üblich – das kann man als kurzes Haken sehen."));
            if (sl != Light.Green)
                e.Tips.Add("Hintergrundprogramme (Overlays, Aufnahme, Browser) schließen und Grafiktreiber aktualisieren – das verringert Ruckler.");
            if (e.Light != Light.Gray && (int)sl > (int)e.Light) e.Light = sl;
        }
        e.Lines.Add(new("Testbedingungen", cond, "Gilt für diese Testszene. Spiele mit mehr Last erreichen weniger Bilder pro Sekunde."));
        return e;
    }

    // ---- Drives / USB -----------------------------------------------------------------------

    private static SectionEval Drives(Session s, Rules rules)
    {
        var drives = s.Results.Where(kv => kv.Key.StartsWith("drive.")).Select(kv => kv.Value).ToList();
        if (drives.Count == 0) return NotRun("drive", "Laufwerke & USB", "Der Laufwerkstest");
        var e = new SectionEval { Id = "drive", Title = "Laufwerke & USB" };
        e.Basis.Add(rules.Laufwerke.Grundlage);
        var lights = new List<Light>();
        var ratings = new List<string>();
        var fazit = new List<string>();

        foreach (var r in drives)
        {
            string letter = r.TestId["drive.".Length..] + ":";
            string kind = r.Info.GetValueOrDefault("drive_kind", "Laufwerk");
            string cls = r.Info.GetValueOrDefault("interface_class", "unknown");
            string clsName = r.Info.GetValueOrDefault("interface", "unbekannt");
            double lanes = 4;
            string key = cls;
            if (cls == "nvme")
            {
                var m = Regex.Match(r.Info.GetValueOrDefault("interface_detail", ""), @"Gen(\d)\s*x(\d+)");
                key = m.Success ? $"nvme_gen{m.Groups[1].Value}" : "";
                if (m.Success) lanes = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            double? rd = M(r, "drive.seq.read"), wr = M(r, "drive.seq.write");
            if (rules.Laufwerke.Klassen.TryGetValue(key, out var dc) && dc.MaxMbS > 0 && rd is { } read)
            {
                double max = cls == "nvme" ? dc.MaxMbS * Math.Min(lanes, 4) / 4 : dc.MaxMbS;
                double pr = read / max * 100, pw = (wr ?? 0) / max * 100;
                var (rating, light) = Rate(pr, rules.Einordnung, rules);
                bool canFaster = r.Info.ContainsKey("interface_note");
                if (canFaster && light == Light.Green) light = Light.Yellow;
                lights.Add(light); ratings.Add(canFaster ? $"{rating} (gemessen an {dc.Name})" : rating);
                e.Basis.Add($"{letter} {kind}: Schnittstelle {clsName}{(r.Info.TryGetValue("interface_detail", out var det) ? $" ({det})" : "")} → " +
                            $"Klassenhöchstwert {F(max, "N0")} MB/s ({dc.Grundlage}{(cls == "nvme" && lanes != 4 ? $", × {lanes}/4 Lanes" : "")}). " +
                            $"Lesen {F(read, "N0")} MB/s = {P(pr)}, Schreiben {F(wr ?? 0, "N0")} MB/s = {P(pw)}.");
                e.Lines.Add(new($"{letter} große Dateien lesen", $"{F(read, "N1")} MB/s = {P(pr)} von {dc.Name}",
                    "Tempo beim Lesen großer Dateien (Filme, Spiele-Installationen). Der Prozentwert zeigt, wie viel die Schnittstelle hergibt."));
                if (wr is { } w)
                    e.Lines.Add(new($"{letter} große Dateien schreiben", $"{F(w, "N1")} MB/s = {P(pw)} von {dc.Name}",
                        pw < pr * 0.5 ? "Schreiben ist deutlich langsamer als Lesen – bei USB-Sticks und günstigen Speichern normal, weil der Speicher selbst bremst."
                                      : "Tempo beim Kopieren großer Dateien auf dieses Laufwerk."));
                if (canFaster)
                {
                    e.Tips.Add($"{letter} {r.Info["interface_note"]} An einen USB-3-Anschluss direkt am PC stecken (meist blau oder mit „SS“ beschriftet) und ein USB-3-Kabel bzw. einen USB-3-Hub verwenden.");
                    fazit.Add($"Das Laufwerk {letter} ({kind.Split(' ')[0]}) ist langsam, weil es nur über USB 2.0 verbunden ist, obwohl es mehr könnte.");
                }
                else if (light != Light.Green)
                    fazit.Add($"Das Laufwerk {letter} nutzt seinen Anschluss nur zu {P(pr)} – das Gerät selbst ist langsamer als die Schnittstelle.");
                else fazit.Add($"Das Laufwerk {letter} nutzt seinen Anschluss gut aus.");
            }
            else
            {
                lights.Add(Light.Gray); ratings.Add("nicht bewertbar");
                e.Basis.Add($"{letter}: Schnittstellenklasse „{clsName}“ nicht in bewertung.json bzw. nicht erkennbar – nicht bewertbar.");
                if (rd is { } r2) e.Lines.Add(new($"{letter} große Dateien lesen", F(r2, "N1") + " MB/s", "Tempo beim Lesen großer Dateien."));
            }
            if (M(r, "drive.4k.q1.read") is { } k)
                e.Lines.Add(new($"{letter} viele kleine Dateien", $"{F(k, "N2")} MB/s",
                    "Tempo bei vielen kleinen Dateien – wichtig für Windows-Start und Ladezeiten. Nicht bewertet: dafür gibt es keine Schnittstellen-Grundlage."));
        }
        var rated = lights.Where(l => l != Light.Gray).ToList();
        if (rated.Count > 0)
        {
            e.Light = rated.Max();
            e.Rating = ratings.Where(x => x != "nicht bewertbar")
                .OrderBy(x => x.StartsWith("schwach") ? 0 : x.StartsWith("ausreichend") ? 1 : x.StartsWith("gut") ? 2 : 3).First();
            e.Basis.Add(ScaleText(rules.Einordnung) + " Ein Gerät, das an einer zu langsamen Schnittstelle hängt, wird höchstens gelb.");
        }
        e.Fazit = fazit.Count > 0 ? string.Join(" ", fazit) : null;
        return e;
    }

    // ---- Controller -------------------------------------------------------------------------

    private static SectionEval Controller(Session s, Rules rules)
    {
        s.Results.TryGetValue("controller", out var r);
        if (r == null) return NotRun("controller", "Controller", "Der Controller-Test");
        var c = rules.Controller;
        var e = new SectionEval { Id = "controller", Title = "Controller" };
        e.Basis.Add(c.Grundlage);
        var parts = new List<string>();
        if (M(r, "ctl.hz.median") is { } hz)
        {
            int cls = c.KlassenHz.OrderBy(k => Math.Abs(Math.Log(hz / k))).First();
            string clsText = cls >= 2000 ? $"{cls} Hz (Klasse 2000–8000 Hz, sehr hohe Abfragerate)" : $"{cls} Hz";
            string rating = hz >= c.SehrGutAbHz ? "sehr gut" : hz >= c.GutAbHz ? "gut" : hz >= c.AusreichendAbHz ? "ausreichend" : "schwach";
            (e.Rating, e.Light) = Label(rating, rules);
            e.Basis.Add($"Polling-Rate {F(hz, "N0")} Hz → Klasse {clsText}. Grenzen: sehr gut ≥ {c.SehrGutAbHz} Hz, gut ≥ {c.GutAbHz} Hz, ausreichend ≥ {c.AusreichendAbHz} Hz.");
            e.Lines.Add(new("Abfragerate", $"{F(hz, "N0")} Hz (Klasse {clsText})",
                $"Der Controller meldet sich etwa {F(hz, "N0")}-mal pro Sekunde beim PC. Höher bedeutet: Eingaben kommen schneller an."));
            parts.Add($"meldet sich etwa {F(Math.Round(hz / 100) * 100, "N0")}-mal pro Sekunde");
            if (hz < c.SehrGutAbHz) e.Tips.Add("Kabel statt Funk verwenden oder in der Hersteller-Software eine höhere Abfragerate (Polling-Rate) einstellen.");

            if (M(r, "ctl.jitter") is { } j)
            {
                // absolute jitter: 8000 Hz with 0.06 ms jitter is very even, regardless of the short packet interval
                string word = j < c.JitterSehrGutBisMs ? "sehr gleichmäßig" : j < c.JitterGutBisMs ? "gleichmäßig" : j < c.JitterAusreichendBisMs ? "leicht unruhig" : "unruhig";
                e.Basis.Add($"Gleichmäßigkeit: Jitter {F(j, "0.000")} ms (sehr gut < {F(c.JitterSehrGutBisMs, "0.###")} ms, gut < {F(c.JitterGutBisMs, "0.###")} ms, " +
                            $"ausreichend < {F(c.JitterAusreichendBisMs, "0.###")} ms).");
                e.Lines.Add(new("Gleichmäßigkeit", $"{word} ({F(j, "0.000")} ms)",
                    j < c.JitterGutBisMs ? "Die Meldungen kommen in fast gleichen Abständen – die Steuerung fühlt sich konstant an."
                                         : "Die Abstände zwischen den Meldungen schwanken spürbar – Eingaben kommen mal früher, mal später an."));
                parts.Add(word);
                if (j >= c.JitterGutBisMs)
                {
                    if (e.Light == Light.Green) e.Light = Light.Yellow;
                    e.Tips.Add("Controller direkt an einen USB-Anschluss am Mainboard stecken (nicht über Hub oder Frontpanel).");
                }
            }
        }        else e.Basis.Add("Polling-Test nicht ausgeführt – Abfragerate nicht bewertbar.");

        foreach (var (key, side) in new[] { ("ctl.drift.l", "Linker Stick"), ("ctl.drift.r", "Rechter Stick") })
        {
            if (M(r, key) is not { } d) continue;
            string word = d < c.DriftKeinBisProzent ? "kein Drift" : d < c.DriftLeichtBisProzent ? "leichter Drift" : d < c.DriftDeutlichBisProzent ? "deutlicher Drift" : "starker Drift";
            e.Lines.Add(new(side, $"{word} ({F(d, "0.0")} %)",
                d < c.DriftKeinBisProzent ? "Der Stick steht in Ruhe still – keine ungewollten Bewegungen im Spiel."
                                          : "Der Stick meldet in Ruhe eine kleine Bewegung – im Spiel kann die Figur oder Kamera langsam wandern."));
            if (d >= c.DriftKeinBisProzent)
            {
                e.Tips.Add($"{side}: Totzone (Deadzone) im Spiel oder in der Controller-Software auf mindestens {Math.Ceiling(d + 1)} % stellen; bei starkem Drift Stick reinigen oder tauschen lassen.");
                if (e.Light == Light.Green) e.Light = Light.Yellow;
            }
            if (key == "ctl.drift.r" || !r.Metrics.Any(m => m.Key == "ctl.drift.r"))
                parts.Add(e.Lines.Where(l => l.Label.EndsWith("Stick")).All(l => l.Value.StartsWith("kein")) ? "die Sticks driften nicht" : "ein Stick driftet leicht");
            e.Basis.Add($"{side}: {F(d, "0.0")} % (kein Drift < {c.DriftKeinBisProzent} %, leicht < {c.DriftLeichtBisProzent} %, deutlich < {c.DriftDeutlichBisProzent} %).");
        }
        if (e.Rating == "nicht bewertbar" && e.Lines.Count > 0) e.Rating = "nur Drift geprüft";
        e.Fazit = parts.Count > 0 ? $"Der Controller {string.Join(", ", parts.Distinct())}." : null;
        return e;
    }

    // ---- Internet ---------------------------------------------------------------------------

    private static SectionEval Internet(Session s, Rules rules)
    {
        var r = s.Results["internet"];
        var ir = rules.Internet;
        var e = new SectionEval { Id = "internet", Title = "Internet" };
        e.Basis.Add(ir.Grundlage);
        double? idle = M(r, "net.ping.ruhe"), pd = M(r, "net.ping.download"), pu = M(r, "net.ping.upload");
        double? bd = M(r, "net.bloat.down"), bu = M(r, "net.bloat.up");
        if (M(r, "net.down.mbps") is { } dm)
            e.Lines.Add(new("Download", $"{F(dm, "N1")} Mbit/s = {F(dm / 8, "N1")} MB/s",
                "Wie schnell Daten aus dem Internet ankommen (Streaming, Downloads, Updates). Nicht bewertet – deine gebuchte Geschwindigkeit ist nicht bekannt."));
        if (M(r, "net.up.mbps") is { } um)
            e.Lines.Add(new("Upload", $"{F(um, "N1")} Mbit/s = {F(um / 8, "N1")} MB/s",
                "Wie schnell du Daten ins Internet schickst (Videocalls, Cloud-Backup, Streamen). Nicht bewertet."));
        if (idle is { } i)
            e.Lines.Add(new("Ping in Ruhe", $"{F(i, "0")} ms", "Die Reaktionszeit zum Messserver, wenn sonst nichts läuft."));
        if (bd is { } || bu is { })
        {
            double worst = Math.Max(bd ?? 0, bu ?? 0);
            string grade = worst < ir.APlusUnterMs ? "A+" : worst < ir.AUnterMs ? "A" : worst < ir.BUnterMs ? "B" : worst < ir.CUnterMs ? "C" : worst < ir.DUnterMs ? "D" : "F";
            e.Rating = $"Note {grade}";
            e.Light = grade is "A+" or "A" ? Light.Green : grade is "B" or "C" ? Light.Yellow : Light.Red;
            e.Basis.Add($"Anstieg unter Download-Last {F(bd ?? 0, "0")} ms, unter Upload-Last {F(bu ?? 0, "0")} ms → schlechterer Wert {F(worst, "0")} ms → Note {grade}. " +
                        $"Grenzen (bewertung.json): A+ < {ir.APlusUnterMs} ms, A < {ir.AUnterMs} ms, B < {ir.BUnterMs} ms, C < {ir.CUnterMs} ms, D < {ir.DUnterMs} ms, sonst F.");
            if (pd is { } d && idle is { } i2)
                e.Lines.Add(new("Ping beim Download", $"{F(d, "0")} ms (+{F(bd ?? 0, "0")} ms)",
                    "So reagiert die Leitung, während jemand etwas herunterlädt. Steigt der Wert stark, ruckeln Spiele und Videocalls."));
            if (pu is { } u)
                e.Lines.Add(new("Ping beim Upload", $"{F(u, "0")} ms (+{F(bu ?? 0, "0")} ms)",
                    "So reagiert die Leitung, während jemand etwas hochlädt (z. B. Fotos in die Cloud)."));
            bool downWorse = (bd ?? 0) >= (bu ?? 0);
            double from = idle ?? 0, to = (downWorse ? pd : pu) ?? 0;
            e.Fazit = worst < ir.AUnterMs
                ? $"Deine Internetleitung bleibt auch unter Last reaktionsschnell (Ping {F(from, "0")} → {F(to, "0")} ms) – Spiele und Videocalls laufen ruhig, auch wenn jemand im Netz lädt."
                : $"Beim {(downWorse ? "Download" : "Upload")} steigt dein Ping von {F(from, "0")} auf {F(to, "0")} ms. Spiele und Videocalls ruckeln, wenn jemand im Netz {(downWorse ? "lädt" : "hochlädt")}.";
            if (worst >= ir.AUnterMs)
                e.Tips.Add("Im Router QoS bzw. SQM (Smart Queue Management, z. B. „Cake“ oder „fq_codel“) einschalten und die Bandbreite knapp unter dem Messwert einstellen. " +
                           "Für Spiele ein Netzwerkkabel statt WLAN nutzen und VPN während des Spielens ausschalten.");
        }
        else e.Basis.Add("Ping unter Last nicht gemessen – Bufferbloat nicht bewertbar.");
        if (r.Info.TryGetValue("server", out var srv)) e.Basis.Add($"Messserver: {srv}{(r.Info.TryGetValue("server_location", out var loc) ? $" ({loc})" : "")}, Ping per {r.Info.GetValueOrDefault("ping_method", "?")}.");
        return e;
    }

    // ---- summary ----------------------------------------------------------------------------

    private static void BuildFazit(Evaluation ev)
    {
        var tested = ev.Sections.Where(x => x.Light != Light.Gray).ToList();
        var notTested = ev.Sections.Where(x => x.Light == Light.Gray && x.Fazit == null).Select(x => x.Title.Split(' ')[0]).ToList();
        int red = tested.Count(x => x.Light == Light.Red), yellow = tested.Count(x => x.Light == Light.Yellow);
        if (tested.Count == 0) ev.Fazit.Add("Noch keine Tests in diesem Lauf – starte einzelne Tests oder „Alles testen“.");
        else if (red + yellow == 0) ev.Fazit.Add("Alle getesteten Bereiche arbeiten so schnell, wie es die eingebaute Hardware zulässt.");
        else ev.Fazit.Add($"{red + yellow} von {tested.Count} getesteten Bereichen {(red + yellow == 1 ? "kann" : "können")} besser laufen – die Tipps stehen weiter unten.");
        foreach (var x in ev.Sections.Where(x => x.Fazit != null).OrderBy(x => x.Light == Light.Green ? 1 : 0))
            if (ev.Fazit.Count < 9) ev.Fazit.Add(x.Fazit!);
        if (notTested.Count > 0 && ev.Fazit.Count < 10) ev.Fazit.Add($"Nicht getestet: {string.Join(", ", notTested)}.");
    }
}
