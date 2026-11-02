using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace Lagzero.Core;

/// <summary>
/// Internet test with M-Lab NDT7 (https://github.com/m-lab/ndt-server/blob/main/spec/ndt7-protocol.md):
/// idle latency, download and upload (one WebSocket connection each, as the protocol specifies) and
/// latency under load (bufferbloat) via a separate small ping connection to the same server.
/// Nothing else is sent anywhere. M-Lab publishes test data incl. the client IP address – the UI asks for consent first.
/// </summary>
public sealed class InternetBenchmark : IBenchmark
{
    public string Id => "internet";
    public string Title => "Internet";

    public const string LocateUrl = "https://locate.measurementlab.net/v2/nearest/ndt/ndt7";
    private const string SubProtocol = "net.measurementlab.ndt.v7";
    private const double TestSeconds = 10, RampUpSeconds = 2, RampDownSeconds = 0.5, IdleSeconds = 4, PingInterval = 0.2;
    private static string UserAgent => $"LAGZERO-Benchmark/{AppInfo.Version} (ndt7; Windows)";

    private sealed record Ping(double TimeS, double Ms, string Phase);

    public Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct) => Task.Run(() => Run(progress, ct), ct);

    private async Task<BenchResult> Run(IProgress<BenchProgress> progress, CancellationToken ct)
    {
        var res = new BenchResult { TestId = Id, Title = "Internet", StartedAt = DateTime.Now };
        var total = Stopwatch.StartNew();
        long bytesDown = 0, bytesUp = 0;
        var pings = new List<Ping>();

        progress.Report(new BenchProgress(0.01, "Messserver suchen (M-Lab Locate) …"));
        var (down, up, host, place) = await Locate(ct);
        res.Info["server"] = host;
        if (place.Length > 0) res.Info["server_location"] = place;
        res.Info["provider"] = "M-Lab NDT7";

        // ping method: ICMP if it answers, otherwise TCP connection setup to port 443
        var prober = await PingProber.Create(host, ct);
        res.Info["ping_method"] = prober.Method;

        try
        {
            // 1) idle latency
            progress.Report(new BenchProgress(0.03, "Ping im Ruhezustand …"));
            await PingFor(prober, "ruhe", IdleSeconds, pings, total, ct, f => progress.Report(new BenchProgress(0.03 + 0.1 * f, "Ping im Ruhezustand …")));

            // 2) download + pings under download load
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pingTask = PingFor(prober, "download", 30, pings, total, cts.Token, null);
                var (mbps, bytes, series) = await Download(down, ct, f => progress.Report(new BenchProgress(0.13 + 0.42 * f, $"Download · {f:P0}")));
                cts.Cancel(); await SafeWait(pingTask);
                bytesDown = bytes;
                res.Add("net.down.mbps", "Download", mbps, "Mbit/s", true, "N1", "Geschwindigkeit");
                res.Add("net.down.mbs", "Download", mbps / 8, "MB/s", true, "N1", "Geschwindigkeit");
                res.Series = series.ToArray();
            }

            // 3) upload + pings under upload load
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pingTask = PingFor(prober, "upload", 30, pings, total, cts.Token, null);
                var (mbps, bytes) = await Upload(up, ct, f => progress.Report(new BenchProgress(0.55 + 0.43 * f, $"Upload · {f:P0}")));
                cts.Cancel(); await SafeWait(pingTask);
                bytesUp = bytes;
                res.Add("net.up.mbps", "Upload", mbps, "Mbit/s", true, "N1", "Geschwindigkeit");
                res.Add("net.up.mbs", "Upload", mbps / 8, "MB/s", true, "N1", "Geschwindigkeit");
            }

            // latency / bufferbloat
            foreach (var (phase, label) in new[] { ("ruhe", "Ruhezustand"), ("download", "unter Download-Last"), ("upload", "unter Upload-Last") })
            {
                var v = pings.Where(p => p.Phase == phase && p.Ms >= 0).Select(p => p.Ms).OrderBy(x => x).ToArray();
                if (v.Length == 0) continue;
                double med = v[v.Length / 2], mean = v.Average(), jit = Math.Sqrt(v.Average(x => (x - mean) * (x - mean)));
                res.Add($"net.ping.{phase}", $"Ping {label} (Median)", med, "ms", false, "0.0", "Latenz");
                res.Add($"net.jitter.{phase}", $"Jitter {label}", jit, "ms", false, "0.0", "Latenz");
            }
            double? idle = M(res, "net.ping.ruhe");
            if (idle is { } i0)
            {
                if (M(res, "net.ping.download") is { } d) res.Add("net.bloat.down", "Anstieg unter Download-Last", Math.Max(0, d - i0), "ms", false, "0.0", "Bufferbloat");
                if (M(res, "net.ping.upload") is { } u) res.Add("net.bloat.up", "Anstieg unter Upload-Last", Math.Max(0, u - i0), "ms", false, "0.0", "Bufferbloat");
            }
            int lost = pings.Count(p => p.Ms < 0);
            if (lost > 0) res.Notes.Add($"{lost} von {pings.Count} Ping-Messungen ohne Antwort (Zeitüberschreitung 1 s).");
        }
        catch (OperationCanceledException) { res.Cancelled = true; }

        res.DurationSec = total.Elapsed.TotalSeconds;
        res.Add("net.data", "Übertragene Datenmenge", (bytesDown + bytesUp) / 1e6, "MB", false, "N0", "Datenverbrauch");
        res.SeriesLabel = "Download-Durchsatz je 250 ms (Mbit/s)";
        res.Notes.Add($"Messserver: M-Lab NDT7 · {host}{(place.Length > 0 ? " (" + place + ")" : "")}. Ping per {prober.Method} zum selben Server, alle {PingInterval * 1000:0} ms.");
        res.Notes.Add($"Je Richtung eine Verbindung (NDT7 misst laut Spezifikation immer mit einer TCP-Verbindung), bis {TestSeconds:0} s; " +
                      $"die ersten {RampUpSeconds:0} s (Anlauf) und die letzten {RampDownSeconds:0.#} s (Auslauf) sind herausgerechnet. 1 MB = 10^6 Byte, 1 Mbit/s = 10^6 Bit/s.");
        res.Notes.Add("M-Lab veröffentlicht jede Messung inklusive IP-Adresse, Datum und Uhrzeit (Datenschutzerklärung: measurementlab.net/privacy).");
        progress.Report(new BenchProgress(res.Cancelled ? 0 : 1, res.Cancelled ? "Abgebrochen" : "Fertig"));
        return res;
    }

    private static double? M(BenchResult r, string k) => r.Metrics.FirstOrDefault(m => m.Key == k)?.Value;

    private static async Task SafeWait(Task t) { try { await t; } catch (OperationCanceledException) { } }

    private static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return h;
    }

    /// <summary>Locate API v2: nearest ndt7 server with access-token URLs.</summary>
    private static async Task<(Uri Down, Uri Up, string Host, string Place)> Locate(CancellationToken ct)
    {
        using var http = Http();
        using var resp = await http.GetAsync(LocateUrl, ct);
        if ((int)resp.StatusCode == 204) throw new InvalidOperationException("M-Lab hat gerade keine freie Kapazität (HTTP 204) – bitte später erneut versuchen.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (!r.TryGetProperty("urls", out var urls)) continue;
            if (!urls.TryGetProperty("wss:///ndt/v7/download", out var d) || !urls.TryGetProperty("wss:///ndt/v7/upload", out var u)) continue;
            string place = "";
            if (r.TryGetProperty("location", out var loc))
                place = string.Join(", ", new[] { "city", "country" }.Select(k => loc.TryGetProperty(k, out var v) ? v.GetString() : null).Where(x => !string.IsNullOrEmpty(x)));
            var down = new Uri(d.GetString()!);
            return (down, new Uri(u.GetString()!), down.Host, place);
        }
        throw new InvalidOperationException("M-Lab Locate lieferte keinen ndt7-Server.");
    }

    private static async Task<ClientWebSocket> Connect(Uri url, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(SubProtocol);
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(url, timeout.Token);
        return ws;
    }

    /// <summary>Download: count received bytes; throughput from the window after ramp-up and before ramp-down.</summary>
    private static async Task<(double Mbps, long Bytes, List<double> Series)> Download(Uri url, CancellationToken ct, Action<double> report)
    {
        using var ws = await Connect(url, ct);
        var buf = new byte[1 << 20];
        var marks = new List<(double T, long Bytes)> { (0, 0) };
        var series = new List<double>();
        long bytes = 0, lastSeriesBytes = 0; double lastSeriesT = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            while (ws.State == WebSocketState.Open && sw.Elapsed.TotalSeconds < 13)   // spec: client MAY close after 13 s
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary) bytes += r.Count;
                double t = sw.Elapsed.TotalSeconds;
                marks.Add((t, bytes));
                if (t - lastSeriesT >= 0.25)
                {
                    series.Add((bytes - lastSeriesBytes) * 8 / (t - lastSeriesT) / 1e6);
                    lastSeriesT = t; lastSeriesBytes = bytes;
                    report(Math.Min(1, t / TestSeconds));
                }
            }
        }
        catch (WebSocketException) when (bytes > 0) { /* server closed without handshake – data so far is valid */ }
        if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        return (Window(marks), bytes, series);
    }

    /// <summary>Upload: send binary messages (8 KiB, doubled up to 1 MiB as the spec suggests) for 10 s;
    /// throughput from the server's own TCPInfo.BytesReceived measurements.</summary>
    private static async Task<(double Mbps, long Bytes)> Upload(Uri url, CancellationToken ct, Action<double> report)
    {
        using var ws = await Connect(url, ct);
        var serverMarks = new List<(double T, long Bytes)> { (0, 0) };
        var sw = Stopwatch.StartNew();
        // read the server's measurement messages concurrently
        var reader = Task.Run(async () =>
        {
            var b = new byte[1 << 16];
            try
            {
                while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
                {
                    var r = await ws.ReceiveAsync(b, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType != WebSocketMessageType.Text || !r.EndOfMessage) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(b.AsMemory(0, r.Count));
                        if (doc.RootElement.TryGetProperty("TCPInfo", out var ti) && ti.TryGetProperty("BytesReceived", out var br) &&
                            ti.TryGetProperty("ElapsedTime", out var et))
                            lock (serverMarks) serverMarks.Add((et.GetInt64() / 1e6, br.GetInt64()));
                    }
                    catch (JsonException) { }
                }
            }
            catch { }
        });

        long sent = 0; int size = 1 << 13;
        var payload = new byte[1 << 20];
        Random.Shared.NextBytes(payload);
        try
        {
            while (sw.Elapsed.TotalSeconds < TestSeconds && ws.State == WebSocketState.Open)
            {
                ct.ThrowIfCancellationRequested();
                await ws.SendAsync(payload.AsMemory(0, size), WebSocketMessageType.Binary, true, ct);
                sent += size;
                if (size < payload.Length && size < sent / 16) size *= 2;
                report(sw.Elapsed.TotalSeconds / TestSeconds);
            }
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (WebSocketException) when (sent > 0) { }
        await Task.WhenAny(reader, Task.Delay(3000, CancellationToken.None));
        List<(double, long)> marks;
        lock (serverMarks) marks = serverMarks.ToList();
        double mbps = marks.Count > 2 ? Window(marks) : sent * 8 / sw.Elapsed.TotalSeconds / 1e6;  // fallback: client-side estimate
        return (mbps, sent);
    }

    /// <summary>Mbit/s between t = ramp-up and t = end − ramp-down.</summary>
    private static double Window(List<(double T, long Bytes)> marks)
    {
        if (marks.Count < 2) return 0;
        double end = marks[^1].T;
        double from = Math.Min(RampUpSeconds, end * 0.3), to = Math.Max(from + 0.5, end - RampDownSeconds);
        var a = marks.LastOrDefault(m => m.T <= from); var b = marks.LastOrDefault(m => m.T <= to);
        return b.T > a.T ? (b.Bytes - a.Bytes) * 8 / (b.T - a.T) / 1e6 : 0;
    }

    private static async Task PingFor(PingProber p, string phase, double seconds, List<Ping> list, Stopwatch total, CancellationToken ct, Action<double>? report)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            ct.ThrowIfCancellationRequested();
            var t0 = sw.Elapsed.TotalSeconds;
            double ms = await p.Probe(ct);
            lock (list) list.Add(new Ping(total.Elapsed.TotalSeconds, ms, phase));
            report?.Invoke(sw.Elapsed.TotalSeconds / seconds);
            double wait = PingInterval - (sw.Elapsed.TotalSeconds - t0);
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
        }
    }

    /// <summary>Small separate latency probe to the test server: ICMP echo, or TCP connection setup time to port 443.</summary>
    private sealed class PingProber
    {
        private readonly string _host;
        private readonly bool _icmp;
        public string Method => _icmp ? "ICMP-Echo" : "TCP-Verbindungsaufbau (Port 443)";

        private PingProber(string host, bool icmp) { _host = host; _icmp = icmp; }

        public static async Task<PingProber> Create(string host, CancellationToken ct)
        {
            using var p = new System.Net.NetworkInformation.Ping();
            for (int i = 0; i < 3; i++)
            {
                try { if ((await p.SendPingAsync(host, 1000)).Status == IPStatus.Success) return new PingProber(host, true); } catch { }
                ct.ThrowIfCancellationRequested();
            }
            return new PingProber(host, false);
        }

        /// <summary>Round trip in ms, or −1 on timeout.</summary>
        public async Task<double> Probe(CancellationToken ct)
        {
            try
            {
                if (_icmp)
                {
                    using var p = new System.Net.NetworkInformation.Ping();
                    var r = await p.SendPingAsync(_host, 1000);
                    return r.Status == IPStatus.Success ? Math.Max(r.RoundtripTime, 0.1) : -1;
                }
                using var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(1000);
                var sw = Stopwatch.StartNew();
                await s.ConnectAsync(_host, 443, to.Token);
                return sw.Elapsed.TotalMilliseconds;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return -1; }
            catch (SocketException) { return -1; }
        }
    }
}
