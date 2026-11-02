using System.Diagnostics;

namespace Lagzero;

/// <summary>Raw capture of one polling test: one entry per new XInput packet.</summary>
public sealed class PollingCapture
{
    public required ControllerInfo Controller { get; init; }
    public required int Requested { get; init; }
    public long[] Timestamps = Array.Empty<long>();   // Stopwatch ticks
    public uint[] Packets = Array.Empty<uint>();
    public XInputGamepad[] States = Array.Empty<XInputGamepad>();
    public int Count;                // packets recorded (intervals = Count - 1)
    public long Polls;               // XInputGetState calls in the loop
    public long SkippedPackets;      // packet number jumped by > 1 between two reads
    public double DurationSec;
    public double TimerResolutionMs;
    public DateTime StartedAt;
    public string? Error;
    public bool Cancelled;

    public double PollsPerSecond => DurationSec > 0 ? Polls / DurationSec : 0;
}

/// <summary>
/// Busy-loop XInput reader. Runs on its own highest-priority thread,
/// detects new reports via dwPacketNumber and timestamps them with QPC.
/// </summary>
public sealed class PollingEngine
{
    private XInputState _latest;
    private volatile int _progress;
    public int Progress => _progress;
    public XInputState Latest => _latest; // torn reads are harmless (display only)

    public Task<PollingCapture> RunAsync(uint index, int packets, CancellationToken ct, double idleTimeoutSec = 15)
    {
        var tcs = new TaskCompletionSource<PollingCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = XInput.Describe(index);
        var th = new Thread(() =>
        {
            try { tcs.SetResult(Measure(info, packets, ct, idleTimeoutSec)); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "LAGZERO polling loop",
        };
        th.Start();
        return tcs.Task;
    }

    private PollingCapture Measure(ControllerInfo info, int packets, CancellationToken ct, double idleTimeoutSec)
    {
        int n = packets + 1; // n packets -> n-1 intervals; +1 so we get exactly `packets` intervals
        var cap = new PollingCapture
        {
            Controller = info,
            Requested = packets,
            Timestamps = new long[n],
            Packets = new uint[n],
            States = new XInputGamepad[n],
            StartedAt = DateTime.Now,
            TimerResolutionMs = WinTimer.CurrentResolutionMs(),
        };
        _progress = 0;

        Thread.BeginThreadAffinity();
        try
        {
            if (XInput.GetState(info.Index, out var s) != XInput.Success)
            {
                cap.Error = "Controller nicht verbunden.";
                return cap;
            }
            // warm up the P/Invoke path + caches
            for (int i = 0; i < 200; i++) XInput.GetState(info.Index, out s);

            uint last = s.PacketNumber;
            long freq = Stopwatch.Frequency;
            long idleLimit = (long)(idleTimeoutSec * freq);
            long start = Stopwatch.GetTimestamp();
            long lastChange = start;
            long polls = 0, skipped = 0;
            int count = 0;
            long[] ts = cap.Timestamps; uint[] pk = cap.Packets; XInputGamepad[] st = cap.States;

            while (count < n)
            {
                long t0 = Stopwatch.GetTimestamp();
                uint r = XInput.GetState(info.Index, out s);
                long t1 = Stopwatch.GetTimestamp();
                polls++;

                if (r != XInput.Success) { cap.Error = "Controller wurde getrennt."; break; }

                if (s.PacketNumber != last)
                {
                    // the state was read somewhere inside the call -> use the midpoint
                    ts[count] = t0 + ((t1 - t0) >> 1);
                    pk[count] = s.PacketNumber;
                    st[count] = s.Gamepad;
                    if (count > 0)
                    {
                        uint diff = s.PacketNumber - last;
                        if (diff > 1) skipped += diff - 1;
                    }
                    last = s.PacketNumber;
                    count++;
                    _latest = s;
                    _progress = count;
                    lastChange = t1;
                }
                else if (t1 - lastChange > idleLimit)
                {
                    cap.Error = $"Keine Eingabe seit {idleTimeoutSec:0} s – Stick während des Tests bewegen.";
                    break;
                }

                if ((polls & 0x3FF) == 0 && ct.IsCancellationRequested) { cap.Cancelled = true; break; }
            }

            cap.Count = count;
            cap.Polls = polls;
            cap.SkippedPackets = skipped;
            cap.DurationSec = (Stopwatch.GetTimestamp() - start) / (double)freq;
        }
        finally
        {
            Thread.EndThreadAffinity();
        }
        return cap;
    }
}

public sealed record FilterSettings(
    int WindowSize = 10,
    double Multiplier = 2.5,
    double SyncTolerancePct = 0.1,
    double WarmupThresholdMs = 20.0,
    double ThresholdFloorMs = 5.0);

public sealed record IntervalStats(int N, double MedianMs, double MeanMs, double MinMs, double MaxMs, double JitterMs, double P99Ms)
{
    public double HzMedian => MedianMs > 0 ? 1000.0 / MedianMs : 0;
    public double HzMean => MeanMs > 0 ? 1000.0 / MeanMs : 0;
}

public sealed class PollingResult
{
    public required PollingCapture Capture { get; init; }
    public required FilterSettings Filter { get; init; }
    public required double[] IntervalsMs { get; init; }
    public required bool[] Keep { get; init; }
    public required IntervalStats Unfiltered { get; init; }
    public required IntervalStats Filtered { get; init; }
    public int Outliers => IntervalsMs.Length - Filtered.N;

    /// <summary>Nearest standard USB polling class for the measured median rate.</summary>
    public int RateClass
    {
        get
        {
            int[] classes = { 125, 250, 500, 1000, 2000, 4000, 8000 };
            double hz = Filtered.HzMedian;
            if (hz <= 0) return 0;
            return classes.OrderBy(c => Math.Abs(Math.Log(hz / c))).First();
        }
    }
}

public static class Analysis
{
    public static PollingResult Analyze(PollingCapture cap, FilterSettings? filter = null)
    {
        filter ??= new FilterSettings();
        int m = Math.Max(0, cap.Count - 1);
        var d = new double[m];
        double toMs = 1000.0 / Stopwatch.Frequency;
        for (int i = 0; i < m; i++) d[i] = (cap.Timestamps[i + 1] - cap.Timestamps[i]) * toMs;

        var keep = FilterOutliers(d, filter);
        var cleaned = d.Where((_, i) => keep[i]).ToArray();
        return new PollingResult
        {
            Capture = cap,
            Filter = filter,
            IntervalsMs = d,
            Keep = keep,
            Unfiltered = Compute(d),
            Filtered = Compute(cleaned),
        };
    }

    /// <summary>
    /// polling2-compatible outlier filter: leading warm-up gaps above WarmupThresholdMs are dropped,
    /// afterwards a value is an outlier if it exceeds max(ThresholdFloorMs, median(last N kept) * Multiplier).
    /// </summary>
    public static bool[] FilterOutliers(double[] d, FilterSettings f)
    {
        var keep = new bool[d.Length];
        var window = new Queue<double>(f.WindowSize);
        var buf = new double[f.WindowSize];
        bool warm = true;
        for (int i = 0; i < d.Length; i++)
        {
            double v = d[i];
            if (warm && v > f.WarmupThresholdMs) continue;
            warm = false;

            double thr = f.ThresholdFloorMs;
            if (window.Count > 0)
            {
                window.CopyTo(buf, 0);
                Array.Sort(buf, 0, window.Count);
                double med = MedianSorted(buf, window.Count);
                thr = Math.Max(f.ThresholdFloorMs, med * f.Multiplier);
            }
            if (v <= thr)
            {
                keep[i] = true;
                if (window.Count == f.WindowSize) window.Dequeue();
                window.Enqueue(v);
            }
        }
        return keep;
    }

    public static IntervalStats Compute(double[] d)
    {
        int n = d.Length;
        if (n == 0) return new IntervalStats(0, 0, 0, 0, 0, 0, 0);
        var s = (double[])d.Clone();
        Array.Sort(s);
        double sum = 0; foreach (var v in s) sum += v;
        double mean = sum / n;
        double sq = 0; foreach (var v in s) sq += (v - mean) * (v - mean);
        double sd = Math.Sqrt(sq / n);
        int p99Idx = Math.Clamp((int)Math.Ceiling(0.99 * n) - 1, 0, n - 1); // nearest-rank
        return new IntervalStats(n, MedianSorted(s, n), mean, s[0], s[n - 1], sd, s[p99Idx]);
    }

    private static double MedianSorted(double[] s, int n) =>
        n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
}
