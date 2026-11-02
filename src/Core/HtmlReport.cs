using System.Globalization;
using System.Net;
using System.Text;

namespace Lagzero.Core;

/// <summary>Self-contained HTML report (no external resources) in the LAGZERO look: plain-language evaluation first, raw values collapsible.</summary>
public static class HtmlReport
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>System info sections that list individual devices – only included on request (privacy).</summary>
    public static readonly string[] DeviceSections = { "USB-Geräte" };

    /// <summary>Copy of the session without the device list (unless requested) – used for HTML and JSON export.</summary>
    public static Session ForExport(Session s, bool includeDevices)
    {
        if (includeDevices) return s;
        var json = s.ToJson();
        var copy = Session.FromJson(json)!;
        copy.SystemInfo = copy.SystemInfo.Where(i => !DeviceSections.Contains(i.Section)).ToList();
        return copy;
    }

    public static string Build(Session s, Evaluation? ev = null, bool includeDevices = false)
    {
        ev ??= Evaluator.Evaluate(s, SafeHistory());
        s = ForExport(s, includeDevices);
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>LAGZERO Benchmark Bericht</title>
<style>
:root{--bg:#08080a;--panel:#121214;--hi:#1c1c1f;--border:#2c2c30;--silver:#ced2d8;--light:#f2f4f7;--dim:#8a8f96;--red:#e10600;--green:#2ec46b;--yellow:#f2c230}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--silver);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
header{padding:28px 32px 18px;border-bottom:2px solid var(--red);background:linear-gradient(#16161a,var(--bg))}
.logo{font:800 44px/1 "Bahnschrift","Segoe UI",sans-serif;letter-spacing:2px;color:var(--light)}
.logo b{color:var(--red);font-weight:800;margin:0 6px}
.slogan{color:var(--red);font:700 12px "Bahnschrift","Segoe UI",sans-serif;letter-spacing:4px;margin-top:6px}
.meta{color:var(--dim);margin-top:10px;font-size:12px}
main{padding:24px 32px;max-width:1500px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,460px),1fr));gap:18px;margin-top:18px}
section{background:var(--panel);border:1px solid var(--border);border-left:3px solid var(--red);padding:14px 18px}
h2{margin:0 0 10px;font:700 13px "Bahnschrift","Segoe UI",sans-serif;letter-spacing:2.5px;color:var(--light)}
h3{margin:14px 0 4px;font:700 11px "Bahnschrift","Segoe UI",sans-serif;letter-spacing:2px;color:var(--dim)}
.fazit p{margin:4px 0;font-size:17px;color:var(--light);padding-left:16px;position:relative}
.fazit p:before{content:"";position:absolute;left:0;top:.6em;width:6px;height:6px;background:var(--red)}
.fazit p.note:before{display:none}.fazit p.note{font-size:12px;color:var(--dim)}
.light{display:flex;align-items:center;gap:12px;margin-bottom:10px}
.dot{width:20px;height:20px;border-radius:50%;flex:none;box-shadow:0 0 0 4px rgba(255,255,255,.04)}
.g{background:var(--green)}.y{background:var(--yellow)}.r{background:var(--red)}.n{background:#46494e}
.rating{font:700 14px "Bahnschrift","Segoe UI",sans-serif;letter-spacing:2px;color:var(--light);text-transform:uppercase}
.line{display:flex;justify-content:space-between;gap:14px;margin-top:8px}.line b{color:var(--light);text-align:right}
.mean{color:var(--dim);font-size:13px;margin:2px 0 0 10px}
.tip{margin-top:10px;padding:8px 10px;background:rgba(225,6,0,.08);border-left:3px solid var(--red);color:var(--light);font-size:14px}
.tip:before{content:"TIPP  ";color:var(--red);font:700 11px "Bahnschrift","Segoe UI",sans-serif;letter-spacing:1.5px}
.basis{color:var(--dim);font-size:12.5px;margin:4px 0}
table{width:100%;border-collapse:collapse}td{padding:4px 6px;border-bottom:1px solid #1e1e22;vertical-align:top}
td.v{text-align:right;font-family:Consolas,monospace;color:var(--light);white-space:nowrap}td.u{color:var(--dim);white-space:nowrap;width:1%}
.note{color:var(--dim);font-size:12px;margin:8px 0 0}.warn{color:#ff5a50}
svg{width:100%;height:160px;background:var(--bg);border:1px solid var(--border);margin-top:10px}
details{margin-top:22px}summary{cursor:pointer;font:700 13px "Bahnschrift","Segoe UI",sans-serif;letter-spacing:2.5px;color:var(--light);
 background:var(--panel);border:1px solid var(--border);padding:12px 16px}summary:hover{border-color:var(--red)}
footer{color:var(--dim);font-size:12px;padding:0 32px 28px}
.full{grid-column:1/-1}
</style></head><body>
""");
        sb.Append($"""
<header><div class="logo">LAG<b>|</b>ZERØ</div><div class="slogan">TESTED. MEASURED. OPTIMIZED.</div>
<div class="meta">LAGZERO Benchmark v{E(s.Version)} · Lauf {E(s.StartedAt.ToString("dd.MM.yyyy HH:mm:ss", De))} · nur gemessene Werte, keine Vergleichsdatenbank</div></header><main>
""");

        // compact system head
        var head = new[] { "Prozessor", "Gesamt (installiert)", "Takt (konfiguriert)", "Mainboard", "Hauptmonitor", "Windows" };
        sb.Append("<section class=\"full\"><h2>SYSTEM</h2><table>");
        foreach (var i in s.SystemInfo.Where(i => head.Contains(i.Label)))
            sb.Append($"<tr><td>{E(i.Label)}</td><td>{E(i.Value)}</td></tr>");
        foreach (var i in s.SystemInfo.Where(i => i.Section == "GPU"))
            sb.Append($"<tr><td>Grafikkarte</td><td>{E(i.Label)}</td></tr>");
        sb.Append("</table></section>");

        // plain-language evaluation
        sb.Append("<section class=\"full fazit\" style=\"margin-top:18px\"><h2>FAZIT</h2>");
        foreach (var l in ev.Fazit) sb.Append($"<p>{E(l)}</p>");
        sb.Append($"<p class=\"note\" style=\"padding:0\">Bewertet wird nur mit nachvollziehbaren Grundlagen – alle Grenzen und Referenzwerte stehen in bewertung.json neben der Programmdatei. Ohne Vergleichsdatenbank gibt es keinen Vergleich mit anderen PCs.</p>");
        if (ev.RulesError != null) sb.Append($"<p class=\"note warn\" style=\"padding:0\">{E(ev.RulesError)}</p>");
        sb.Append("</section><div class=\"grid\">");
        foreach (var sec in ev.Sections)
        {
            string dot = sec.Light switch { Light.Green => "g", Light.Yellow => "y", Light.Red => "r", _ => "n" };
            sb.Append($"<section><h2>{E(sec.Title.ToUpperInvariant())}</h2><div class=\"light\"><span class=\"dot {dot}\" title=\"Ampel\"></span><span class=\"rating\">{E(sec.Rating)}</span></div>");
            foreach (var l in sec.Lines)
                sb.Append($"<div class=\"line\"><span>{E(l.Label)}</span><b>{E(l.Value)}</b></div><p class=\"mean\">Was heißt das? {E(l.Meaning)}</p>");
            foreach (var t in sec.Tips) sb.Append($"<div class=\"tip\">{E(t)}</div>");
            sb.Append("<h3>GRUNDLAGE DER BEWERTUNG</h3>");
            foreach (var b in sec.Basis) sb.Append($"<p class=\"basis\">{E(b)}</p>");
            sb.Append("</section>");
        }
        sb.Append("</div>");

        // raw values
        sb.Append("<details><summary>DETAILS · ROHWERTE ANZEIGEN</summary><div class=\"grid\">");
        sb.Append("<section class=\"full\"><h2>SYSTEM (VOLLSTÄNDIG)</h2><table>");
        foreach (var g in s.SystemInfo.GroupBy(i => i.Section))
        {
            sb.Append($"<tr><td colspan=2><h3>{E(g.Key.ToUpperInvariant())}</h3></td></tr>");
            foreach (var i in g) sb.Append($"<tr><td>{E(i.Label)}</td><td>{E(i.Value)}</td></tr>");
        }
        sb.Append("</table></section>");
        foreach (var r in s.Results.Values.OrderBy(r => Order(r.TestId)))
        {
            sb.Append($"<section><h2>{E(r.Title.ToUpperInvariant())}</h2>");
            if (r.Cancelled) sb.Append("<p class=\"note warn\">Test wurde abgebrochen – Werte unvollständig.</p>");
            if (r.Error != null) sb.Append($"<p class=\"note warn\">Fehler: {E(r.Error)}</p>");
            sb.Append("<table>");
            var cond = InfoLabels.Rows(r.Info).ToList();
            if (cond.Count > 0)
            {
                sb.Append("<tr><td colspan=3><h3>TESTBEDINGUNGEN</h3></td></tr>");
                foreach (var (label, value) in cond) sb.Append($"<tr><td>{E(label)}</td><td class=v colspan=2>{E(value)}</td></tr>");
            }
            string? grp = null;
            foreach (var m in r.Metrics)
            {
                if (m.Group != grp && m.Group != null) { grp = m.Group; sb.Append($"<tr><td colspan=3><h3>{E(grp.ToUpperInvariant())}</h3></td></tr>"); }
                sb.Append($"<tr><td>{E(m.Label)}</td><td class=v>{E(m.Value.ToString(m.Format, De))}</td><td class=u>{E(m.Unit)}</td></tr>");
            }
            sb.Append("</table>");
            if (r.Series is { Length: > 1 } ser) sb.Append(Svg(ser, r.SeriesLabel ?? ""));
            foreach (var n in r.Notes)
                sb.Append($"<p class=\"note{(n.StartsWith("⚠") ? " warn" : "")}\">{E(n)}</p>");
            sb.Append($"<p class=\"note\">Gestartet {E(r.StartedAt.ToString("HH:mm:ss", De))} · Dauer {E(r.DurationSec.ToString("0.0", De))} s</p></section>");
        }
        sb.Append("</div></details>");
        sb.Append($"</main><footer>Erstellt mit LAGZERO Benchmark · {E(DateTime.Now.ToString("dd.MM.yyyy HH:mm", De))}</footer></body></html>");
        return sb.ToString();
    }

    private static IEnumerable<Session> SafeHistory()
    {
        try { return HistoryStore.LoadAll().Select(x => x.Session).ToList(); } catch { return Array.Empty<Session>(); }
    }

    public static int Order(string id) => id switch
    {
        "cpu" => 0, "ram" => 1, "gpu" => 2, "frametime" => 3, "controller" => 9,
        _ when id.StartsWith("drive") => 5, _ => 8,
    };

    private static string Svg(double[] v, string label)
    {
        const int W = 1000, H = 160;
        var sorted = v.OrderBy(x => x).ToArray();
        double max = Math.Max(sorted[(int)(sorted.Length * 0.999)] * 1.15, sorted[sorted.Length / 2] * 2.2);
        if (max <= 0) return "";
        var pts = new StringBuilder();
        int step = Math.Max(1, v.Length / 2000);
        for (int i = 0; i < v.Length; i += step)
        {
            double x = i * (double)W / (v.Length - 1);
            double y = H - Math.Min(v[i], max) / max * (H - 10);
            pts.Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
        }
        double med = sorted[sorted.Length / 2];
        double my = H - med / max * (H - 10);
        return $"""
<svg viewBox="0 0 {W} {H}" preserveAspectRatio="none" role="img" aria-label="{E(label)}">
<line x1="0" x2="{W}" y1="{my.ToString("0.#", CultureInfo.InvariantCulture)}" y2="{my.ToString("0.#", CultureInfo.InvariantCulture)}" stroke="#e10600" stroke-dasharray="6 5" stroke-width="1"/>
<polyline fill="none" stroke="#ced2d8" stroke-width="1" vector-effect="non-scaling-stroke" points="{pts}"/></svg>
<p class="note">{E(label)} · Maximum {E(v.Max().ToString("0.00", De))} · Median {E(med.ToString("0.00", De))} (rote Linie)</p>
""";
    }
}
