using Lagzero.Core;
using Lagzero.UI;

namespace Lagzero;

/// <summary>Additional test pages in "Alles testen" order (after CPU and RAM). The drive test runs last because it asks for confirmation.</summary>
internal static class ExtraPages
{
    public static RenderOptions? GpuOptions, FrametimeOptions;

    /// <summary>Data estimate (from the last own measurement if available) + M-Lab privacy notice; true only after an explicit click.</summary>
    private static bool ConfirmInternet(MainForm owner)
    {
        string estimate;
        var last = HistoryStore.LoadAll().Select(x => x.Session).Select(s => s.Results.GetValueOrDefault("internet"))
                               .FirstOrDefault(r => r != null && !r.Cancelled);
        double? d = last?.Metrics.FirstOrDefault(m => m.Key == "net.down.mbps")?.Value, u = last?.Metrics.FirstOrDefault(m => m.Key == "net.up.mbps")?.Value;
        if (d is { } dv && u is { } uv)
            estimate = $"Geschätzte Datenmenge: ca. {((dv + uv) * 10 / 8):N0} MB (aus deiner letzten Messung: {dv:N0} Mbit/s Download, {uv:N0} Mbit/s Upload, je 10 s).";
        else
            estimate = "Geschätzte Datenmenge: je 10 s Download und Upload – ca. 125 MB pro 100 Mbit/s Leitungsgeschwindigkeit " +
                       "(bei 1 Gbit/s Download und 50 Mbit/s Upload ca. 1,3 GB).";
        string msg =
            estimate + "\n\n" +
            "Messanbieter: Measurement Lab (M-Lab), NDT7. Hinweis zum Datenschutz: M-Lab speichert jede Messung dauerhaft und veröffentlicht sie " +
            "öffentlich – einschließlich deiner IP-Adresse, Datum und Uhrzeit (measurementlab.net/privacy).\n\n" +
            "Außer diesen Testverbindungen sendet LAGZERO nichts ins Internet. Der Test lässt sich jederzeit abbrechen.";
        return ConfirmDialog.Ask(owner, "LAGZERO · Internettest bestätigen", msg, "JA, TEST STARTEN", false, "INTERNETTEST BESTÄTIGEN");
    }

    public static IEnumerable<(string Id, string Title, TestPage Page)> Create(MainForm owner)
    {
        var gpuOpt = GpuOptions = new RenderOptions(withVsync: false) { Status = owner.SetStatus };
        yield return ("gpu", "GPU", new OptionsTestPage("gpu", "GPU-BENCHMARK",
            "3D-Render-Test mit Direct3D 11 in einem eigenen Testfenster: feste Szene mit tausenden beleuchteten Würfeln, ohne VSync – " +
            "gemessen werden FPS und Frametime (3 s Aufwärmen + 12 s Messung). Danach ein Compute-Test (FP32-FMA, GPU-Zeitstempel). " +
            "Esc im Testfenster bricht ab.", gpuOpt, 92)
        { Factory = () => new GpuBenchmark(gpuOpt.Current()) });

        var ftOpt = FrametimeOptions = new RenderOptions(withVsync: true) { Status = owner.SetStatus };
        yield return ("frametime", "FRAMETIME", new OptionsTestPage("frametime", "FRAMETIME-TEST",
            "Eigenes Testfenster, 10 s Messung mit mittlerer Last: FPS Ø, 1 % / 0,1 % Low, Ruckler und Frametime-Verlauf. " +
            "Ohne VSync zeigt der Test die reine Gleichmäßigkeit von CPU, GPU und Treiber, mit VSync die tatsächliche Bildausgabe am Monitor.",
            ftOpt, 92)
        { Factory = () => new FrametimeBenchmark(ftOpt.Current()) });

        yield return ("drive", "LAUFWERKE", DrivePage.Build());

        TestPage? net = null;
        net = new TestPage("internet", "INTERNET · DOWNLOAD, UPLOAD, BUFFERBLOAT",
            "Misst Download und Upload über M-Lab NDT7 (je bis 10 s, eine Verbindung je Richtung) und den Ping im Ruhezustand sowie unter " +
            "Download- und Upload-Last (Bufferbloat, Note A+ bis F). Läuft nur auf eigenen Klick – nicht bei „Alles testen“. " +
            "M-Lab veröffentlicht jede Messung inklusive IP-Adresse; vor jedem Start wird um Zustimmung gefragt.", chart: true)
        {
            ExcludeFromAll = true,
            Factory = () => ConfirmInternet(owner) ? new InternetBenchmark() : null,
        };
        if (net.Chart != null) net.Chart.Unit = "Mbit/s";
        yield return ("internet", "INTERNET", net);
    }
}


/// <summary>Test page whose settings are locked while any test runs.</summary>
internal sealed class OptionsTestPage(string id, string title, string desc, RenderOptions options, int height)
    : TestPage(id, title, desc, options, height, chart: true)
{
    protected override void OnBusyChanged(bool anyBusy) => options.SetEnabled(!anyBusy);
}
