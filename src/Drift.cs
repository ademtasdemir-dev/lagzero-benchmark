using System.Diagnostics;

namespace Lagzero;

public sealed record StickDrift(
    double MeanX, double MeanY,       // normalized -1..1 (resting offset)
    double MaxRadius,                 // largest distance from center seen, normalized
    double NoiseX, double NoiseY,     // standard deviation, normalized
    short RawMinX, short RawMaxX, short RawMinY, short RawMaxY)
{
    public double OffsetPct => Math.Sqrt(MeanX * MeanX + MeanY * MeanY) * 100.0;
    public double MaxRadiusPct => MaxRadius * 100.0;
    public double NoisePct => Math.Max(NoiseX, NoiseY) * 100.0;

    public string Verdict => MaxRadiusPct switch
    {
        < 2.0 => "SAUBER",
        < 5.0 => "LEICHT",
        < 10.0 => "MITTEL",
        _ => "STARK",
    };

    /// <summary>Recommended radial deadzone in % (max deflection at rest + 1 % margin).</summary>
    public int RecommendedDeadzonePct => (int)Math.Ceiling(MaxRadiusPct + 1.0);
}

public sealed record DriftResult(StickDrift Left, StickDrift Right, int Samples, double Seconds, DateTime StartedAt);

public static class DriftTest
{
    public static Task<DriftResult> RunAsync(uint index, double seconds, IProgress<double>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(index, seconds, progress, ct), ct,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static DriftResult Run(uint index, double seconds, IProgress<double>? progress, CancellationToken ct)
    {
        var lx = new List<short>(8000); var ly = new List<short>(8000);
        var rx = new List<short>(8000); var ry = new List<short>(8000);
        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();
        double lastReport = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            ct.ThrowIfCancellationRequested();
            if (XInput.GetState(index, out var s) != XInput.Success)
                throw new InvalidOperationException("Controller wurde getrennt.");
            lx.Add(s.Gamepad.LX); ly.Add(s.Gamepad.LY);
            rx.Add(s.Gamepad.RX); ry.Add(s.Gamepad.RY);
            double t = sw.Elapsed.TotalSeconds;
            if (t - lastReport > 0.05) { progress?.Report(t / seconds); lastReport = t; }
            Thread.Sleep(1); // ~1 kHz sampling with 1 ms timer resolution
        }
        progress?.Report(1.0);
        return new DriftResult(Eval(lx, ly), Eval(rx, ry), lx.Count, sw.Elapsed.TotalSeconds, started);
    }

    private static double Norm(short v) => v < 0 ? v / 32768.0 : v / 32767.0;

    private static StickDrift Eval(List<short> xs, List<short> ys)
    {
        int n = xs.Count;
        if (n == 0) return new StickDrift(0, 0, 0, 0, 0, 0, 0, 0, 0);
        double mx = 0, my = 0, maxR = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Norm(xs[i]), y = Norm(ys[i]);
            mx += x; my += y;
            maxR = Math.Max(maxR, Math.Sqrt(x * x + y * y));
        }
        mx /= n; my /= n;
        double vx = 0, vy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = Norm(xs[i]) - mx, dy = Norm(ys[i]) - my;
            vx += dx * dx; vy += dy * dy;
        }
        return new StickDrift(mx, my, maxR, Math.Sqrt(vx / n), Math.Sqrt(vy / n),
            xs.Min(), xs.Max(), ys.Min(), ys.Max());
    }
}
