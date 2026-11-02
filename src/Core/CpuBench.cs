using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Lagzero.Core;

/// <summary>
/// CPU benchmark with a FIXED amount of work per test: the duration is measured and the score is the
/// resulting throughput (1 point = 1 million operations per second). Integer, floating point and SIMD,
/// each single-threaded and on all logical processors.
/// </summary>
public sealed class CpuBenchmark : IBenchmark
{
    public string Id => "cpu";
    public string Title => "CPU";

    // fixed workloads (iterations per thread); ~1–2 s single core on a current desktop CPU
    private const long IntIters = 1_200_000_000;
    private const long FloatIters = 1_200_000_000;
    private const long SimdIters = 2_400_000_000;
    private const int Chunks = 100;

    private const double IntOpsPerIter = 14;        // see IntKernel
    private const double FloatFlopsPerIter = 16;    // 8 independent mul+add

    private static volatile float _sinkF;
    private static long _sinkL;

    private enum Kind { Int, Float, Simd }

    public static string SimdName
    {
        get
        {
            if (Vector512.IsHardwareAccelerated) return "AVX-512 (512 Bit)";
            if (Vector256.IsHardwareAccelerated) return System.Runtime.Intrinsics.X86.Fma.IsSupported ? "AVX2 + FMA (256 Bit)" : "AVX (256 Bit, ohne FMA: Multiplikation + Addition getrennt)";
            return "SSE (128 Bit, ohne FMA: Multiplikation + Addition getrennt)";
        }
    }

    private static int SimdLanes => Vector512.IsHardwareAccelerated ? 16 : Vector256.IsHardwareAccelerated ? 8 : 4;

    public Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private BenchResult Run(IProgress<BenchProgress> progress, CancellationToken ct)
    {
        var res = new BenchResult { TestId = Id, Title = "CPU", StartedAt = DateTime.Now };
        var total = Stopwatch.StartNew();
        int threads = Environment.ProcessorCount;
        var steps = new (Kind kind, string name, string key, bool multi)[]
        {
            (Kind.Int, "Ganzzahl", "int", false), (Kind.Int, "Ganzzahl", "int", true),
            (Kind.Float, "Fließkomma", "float", false), (Kind.Float, "Fließkomma", "float", true),
            (Kind.Simd, "SIMD", "simd", false), (Kind.Simd, "SIMD", "simd", true),
        };
        // warm-up: JIT all kernels at full optimization
        IntKernel(1000); FloatKernel(1000); SimdKernel(1000);

        var single = new Dictionary<string, double>();
        try
        {
            for (int i = 0; i < steps.Length; i++)
            {
                var (kind, name, key, multi) = steps[i];
                string mode = multi ? $"Mehrkern ({threads} Threads)" : "Einzelkern";
                int n = multi ? threads : 1;
                double baseFrac = i / (double)steps.Length;
                progress.Report(new BenchProgress(baseFrac, $"{name} · {mode} …"));

                double sec = Measure(kind, n, ct, f => progress.Report(new BenchProgress(baseFrac + f / steps.Length, $"{name} · {mode} · {f:P0}")));
                long iters = Iters(kind) * n;
                double opsPerIter = kind switch { Kind.Int => IntOpsPerIter, Kind.Float => FloatFlopsPerIter, _ => 16.0 * SimdLanes };
                double mops = iters * opsPerIter / sec / 1e6;

                string unit = kind == Kind.Int ? "Punkte (MOps/s)" : "Punkte (MFLOPS)";
                string m = multi ? "Mehrkern" : "Einzelkern";
                res.Add($"cpu.{key}.{(multi ? "multi" : "single")}", m, mops, unit, true, "N0", name);
                res.Add($"cpu.{key}.{(multi ? "multi" : "single")}.time", $"{m} · Dauer", sec, "s", false, "0.000", name);
                if (!multi) single[key] = mops;
                else if (single.TryGetValue(key, out var s1) && s1 > 0)
                    res.Add($"cpu.{key}.scaling", "Skalierung Mehrkern / Einzelkern", mops / s1, "×", true, "0.00", name);
            }
            res.Add("cpu.total.time", "Gesamtdauer", total.Elapsed.TotalSeconds, "s", false, "0.0", "Gesamt");
        }
        catch (OperationCanceledException) { res.Cancelled = true; }

        res.DurationSec = total.Elapsed.TotalSeconds;
        res.Info["cpu"] = SystemInfo.CpuName();
        res.Info["method"] = "v2-queue";   // multi-core via shared work queue (runs before this were measured differently)
        int cores = CpuTopology.Sets.Select(s => s.CoreIndex).Distinct().Count();
        if (cores > 0) res.Info["cores"] = cores.ToString(System.Globalization.CultureInfo.InvariantCulture);
        res.Info["threads"] = threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (CpuTopology.IsHybrid) { var (pc, ec) = CpuTopology.CoreCounts(); res.Info["p_cores"] = pc.ToString(); res.Info["e_cores"] = ec.ToString(); }
        res.Notes.Add($"Feste Arbeitsmenge je Test, gemessen wird die Dauer. 1 Punkt = 1 Mio. Operationen/s (Ganzzahl) bzw. 1 MFLOPS (Fließkomma/SIMD).");
        res.Notes.Add($"SIMD-Befehlssatz: {SimdName}. Mehrkern = {threads} Threads ziehen Arbeitsblöcke aus einer gemeinsamen Warteschlange (Gesamtarbeit = {threads} × Einzelkern-Arbeit).");
        if (CpuTopology.IsHybrid)
        {
            var (p, e) = CpuTopology.CoreCounts();
            res.Notes.Add($"Hybrid-CPU erkannt ({p} P-Kerne, {e} E-Kerne): Einzelkern-Tests laufen auf den P-Kernen.");
        }
        progress.Report(new BenchProgress(res.Cancelled ? 0 : 1, res.Cancelled ? "Abgebrochen" : "Fertig"));
        return res;
    }

    private static long Iters(Kind k) => k switch { Kind.Int => IntIters, Kind.Float => FloatIters, _ => SimdIters };

    /// <summary>Runs the fixed workload on n threads, returns wall time in seconds.</summary>
    private static double Measure(Kind kind, int n, CancellationToken ct, Action<double> report)
    {
        // shared work queue: n × fixed work, split into small chunks that every thread pulls until empty.
        // Fast (P) cores simply take more chunks, so the wall time reflects the total throughput of the CPU.
        int chunksPerUnit = n == 1 ? Chunks : Chunks * 4;
        long chunk = Iters(kind) / chunksPerUnit;
        long totalChunks = (long)chunksPerUnit * n;
        long next = 0, done = 0;
        using var start = new Barrier(n + 1);
        var ths = new Thread[n];
        Exception? err = null;
        for (int t = 0; t < n; t++)
        {
            ths[t] = new Thread(() =>
            {
                try
                {
                    if (n == 1) CpuTopology.PinCurrentThreadToPerformanceCores();
                    start.SignalAndWait();
                    while (Interlocked.Increment(ref next) <= totalChunks)
                    {
                        if (ct.IsCancellationRequested) return;
                        switch (kind)
                        {
                            case Kind.Int: Interlocked.Add(ref _sinkL, (long)IntKernel(chunk)); break;
                            case Kind.Float: _sinkF += (float)FloatKernel(chunk); break;
                            default: _sinkF += SimdKernel(chunk); break;
                        }
                        Interlocked.Increment(ref done);
                    }
                }
                catch (Exception ex) { err = ex; }
            })
            { IsBackground = true, Priority = n == 1 ? ThreadPriority.AboveNormal : ThreadPriority.Normal };
            ths[t].Start();
        }
        start.SignalAndWait();
        var sw = Stopwatch.StartNew();
        while (!ths.All(t => !t.IsAlive))
        {
            Thread.Sleep(50);
            report(Interlocked.Read(ref done) / (double)totalChunks);
        }
        double sec = sw.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();
        if (err != null) throw err;
        return sec;
    }

    // ----- kernels -------------------------------------------------------------------------

    /// <summary>LCG + xorshift + mixing: 14 integer ops per iteration (mul, add, shifts, xors).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IntKernel(long iters)
    {
        ulong a = 1, b = 0x9E3779B97F4A7C15UL, c = 3, d = 4;
        for (long i = 0; i < iters; i++)
        {
            a = a * 6364136223846793005UL + 1442695040888963407UL;   // 2
            b ^= b << 13; b ^= b >> 7; b ^= b << 17;                 // 6
            c += a ^ (b >> 3);                                       // 3
            d = (d ^ c) * 0x9E3779B97F4A7C15UL + (ulong)i;           // 3
        }
        return a ^ b ^ c ^ d;
    }

    /// <summary>8 independent multiply-add chains (scalar double), 16 FLOPs per iteration.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double FloatKernel(long iters)
    {
        double x0 = 1, x1 = 2, x2 = 3, x3 = 4, x4 = 5, x5 = 6, x6 = 7, x7 = 8;
        const double m = 0.9999999, k = 0.5;
        for (long i = 0; i < iters; i++)
        {
            x0 = x0 * m + k; x1 = x1 * m + k; x2 = x2 * m + k; x3 = x3 * m + k;
            x4 = x4 * m + k; x5 = x5 * m + k; x6 = x6 * m + k; x7 = x7 * m + k;
        }
        return x0 + x1 + x2 + x3 + x4 + x5 + x6 + x7;
    }

    /// <summary>8 independent vector FMA chains at the widest accelerated width: 16 × lanes FLOPs per iteration.</summary>
    private static float SimdKernel(long iters) =>
        Vector512.IsHardwareAccelerated ? Simd512(iters) :
        Vector256.IsHardwareAccelerated && System.Runtime.Intrinsics.X86.Fma.IsSupported ? Simd256(iters) :
        Vector256.IsHardwareAccelerated ? Simd256NoFma(iters) : Simd128(iters);

    /// <summary>AVX without FMA: separate multiply and add (same FLOP count, no slow software FMA emulation).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Simd256NoFma(long iters)
    {
        var m = Vector256.Create(0.9999999f); var k = Vector256.Create(0.5f);
        Vector256<float> a0 = Vector256.Create(1f), a1 = Vector256.Create(2f), a2 = Vector256.Create(3f), a3 = Vector256.Create(4f),
                         a4 = Vector256.Create(5f), a5 = Vector256.Create(6f), a6 = Vector256.Create(7f), a7 = Vector256.Create(8f);
        for (long i = 0; i < iters; i++)
        {
            a0 = a0 * m + k; a1 = a1 * m + k; a2 = a2 * m + k; a3 = a3 * m + k;
            a4 = a4 * m + k; a5 = a5 * m + k; a6 = a6 * m + k; a7 = a7 * m + k;
        }
        return Vector256.Sum(a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Simd256(long iters)
    {
        var m = Vector256.Create(0.9999999f); var k = Vector256.Create(0.5f);
        Vector256<float> a0 = Vector256.Create(1f), a1 = Vector256.Create(2f), a2 = Vector256.Create(3f), a3 = Vector256.Create(4f),
                         a4 = Vector256.Create(5f), a5 = Vector256.Create(6f), a6 = Vector256.Create(7f), a7 = Vector256.Create(8f);
        for (long i = 0; i < iters; i++)
        {
            a0 = Vector256.FusedMultiplyAdd(a0, m, k); a1 = Vector256.FusedMultiplyAdd(a1, m, k);
            a2 = Vector256.FusedMultiplyAdd(a2, m, k); a3 = Vector256.FusedMultiplyAdd(a3, m, k);
            a4 = Vector256.FusedMultiplyAdd(a4, m, k); a5 = Vector256.FusedMultiplyAdd(a5, m, k);
            a6 = Vector256.FusedMultiplyAdd(a6, m, k); a7 = Vector256.FusedMultiplyAdd(a7, m, k);
        }
        return Vector256.Sum(a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Simd512(long iters)
    {
        var m = Vector512.Create(0.9999999f); var k = Vector512.Create(0.5f);
        Vector512<float> a0 = Vector512.Create(1f), a1 = Vector512.Create(2f), a2 = Vector512.Create(3f), a3 = Vector512.Create(4f),
                         a4 = Vector512.Create(5f), a5 = Vector512.Create(6f), a6 = Vector512.Create(7f), a7 = Vector512.Create(8f);
        for (long i = 0; i < iters; i++)
        {
            a0 = Vector512.FusedMultiplyAdd(a0, m, k); a1 = Vector512.FusedMultiplyAdd(a1, m, k);
            a2 = Vector512.FusedMultiplyAdd(a2, m, k); a3 = Vector512.FusedMultiplyAdd(a3, m, k);
            a4 = Vector512.FusedMultiplyAdd(a4, m, k); a5 = Vector512.FusedMultiplyAdd(a5, m, k);
            a6 = Vector512.FusedMultiplyAdd(a6, m, k); a7 = Vector512.FusedMultiplyAdd(a7, m, k);
        }
        return Vector512.Sum(a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Simd128(long iters)
    {
        var m = Vector128.Create(0.9999999f); var k = Vector128.Create(0.5f);
        Vector128<float> a0 = Vector128.Create(1f), a1 = Vector128.Create(2f), a2 = Vector128.Create(3f), a3 = Vector128.Create(4f),
                         a4 = Vector128.Create(5f), a5 = Vector128.Create(6f), a6 = Vector128.Create(7f), a7 = Vector128.Create(8f);
        for (long i = 0; i < iters; i++)
        {
            a0 = a0 * m + k; a1 = a1 * m + k;
            a2 = a2 * m + k; a3 = a3 * m + k;
            a4 = a4 * m + k; a5 = a5 * m + k;
            a6 = a6 * m + k; a7 = a7 * m + k;
        }
        return Vector128.Sum(a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }
}
