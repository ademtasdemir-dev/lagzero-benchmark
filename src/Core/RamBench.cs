using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lagzero.Core;

/// <summary>
/// RAM bandwidth (read / write / copy, all threads, buffers far larger than any CPU cache)
/// and latency (random pointer chasing over 64-byte cache lines).
/// </summary>
public sealed unsafe class RamBenchmark : IBenchmark
{
    public string Id => "ram";
    public string Title => "RAM";

    private const double RoundSeconds = 0.8;
    private const int Rounds = 3;
    private static long _sink;

    public Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private BenchResult Run(IProgress<BenchProgress> progress, CancellationToken ct)
    {
        var res = new BenchResult { TestId = Id, Title = "RAM", StartedAt = DateTime.Now };
        var total = Stopwatch.StartNew();
        var (_, avail, _) = SystemInfo.MemoryStatus();

        long size = 512L << 20;                                   // per buffer
        while (size * 2 > (long)(avail * 0.5) && size > (64L << 20)) size /= 2;
        int threads = Math.Min(Environment.ProcessorCount, 32);
        long slice = size / threads / 4096 * 4096;

        byte* a = null, b = null;
        try
        {
            progress.Report(new BenchProgress(0, $"Puffer anlegen ({size >> 20} MB × 2) …"));
            a = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
            b = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
            NativeMemory.Fill(a, (nuint)size, 0x5A);              // commit all pages
            NativeMemory.Fill(b, (nuint)size, 0xA5);
            ct.ThrowIfCancellationRequested();

            nint pa = (nint)a, pb = (nint)b;
            progress.Report(new BenchProgress(0.02, "Lesen …"));
            double read = Bandwidth(threads, ct, t => { ReadSlice((byte*)(pa + (nint)(t * slice)), slice); return slice; },
                f => progress.Report(new BenchProgress(0.02 + 0.23 * f, "Lesen …")));
            res.Add("ram.read", "Lesen", read, "MB/s", true, "N0", "Bandbreite");

            double write = Bandwidth(threads, ct, t => { WriteSlice((byte*)(pa + (nint)(t * slice)), slice); return slice; },
                f => progress.Report(new BenchProgress(0.25 + 0.25 * f, "Schreiben …")));
            res.Add("ram.write", "Schreiben", write, "MB/s", true, "N0", "Bandbreite");

            double copy = Bandwidth(threads, ct, t => { Buffer.MemoryCopy((byte*)(pa + (nint)(t * slice)), (byte*)(pb + (nint)(t * slice)), slice, slice); return slice; },
                f => progress.Report(new BenchProgress(0.50 + 0.25 * f, "Kopieren …")));
            res.Add("ram.copy", "Kopieren", copy, "MB/s", true, "N0", "Bandbreite");

            progress.Report(new BenchProgress(0.75, "Latenz …"));
            double lat = Latency(a, size, ct, f => progress.Report(new BenchProgress(0.75 + 0.25 * f, "Latenz …")));
            res.Add("ram.latency", "Zugriffslatenz", lat, "ns", false, "0.0", "Latenz");
        }
        catch (OperationCanceledException) { res.Cancelled = true; }
        finally
        {
            if (a != null) NativeMemory.AlignedFree(a);
            if (b != null) NativeMemory.AlignedFree(b);
        }

        res.DurationSec = total.Elapsed.TotalSeconds;
        res.Notes.Add($"Puffer {size >> 20} MB je Richtung, {threads} Threads, bester von {Rounds} Durchgängen. 1 MB = 10^6 Byte. " +
                      $"Schreiben {(Avx.IsSupported ? "mit Non-Temporal-Stores (AVX)" : "mit 128-Bit-Befehlen (ohne AVX)")}, Kopieren zählt die kopierte Datenmenge einmal.");
        res.Notes.Add("Latenz: zufällige Verkettung über 64-Byte-Cachelines im ganzen Puffer (4-KB-Seiten, TLB-Fehlgriffe enthalten).");
        res.Notes.AddRange(MemoryHints());
        var mods = SystemInfo.MemoryModules();
        int mts = mods.Count > 0 ? mods.Max(m => m.ConfiguredMts) : 0;
        if (mts > 0) res.Info["ram_mts"] = mts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (ch, basis) = SystemInfo.MemoryChannels(mods);
        if (ch is { } chn) res.Info["ram_channels"] = chn.ToString(System.Globalization.CultureInfo.InvariantCulture);
        res.Info["ram_channels_basis"] = basis;
        res.Info["ram_modules"] = mods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int rated = mods.Count > 0 ? mods.Max(m => m.PartNumberMts) : 0;
        if (rated > 0) { res.Info["ram_label_mts"] = rated.ToString(System.Globalization.CultureInfo.InvariantCulture); res.Info["ram_partnumber"] = mods[0].PartNumber.Trim(); }
        progress.Report(new BenchProgress(res.Cancelled ? 0 : 1, res.Cancelled ? "Abgebrochen" : "Fertig"));
        return res;
    }

    /// <summary>Hints about unused / reserved RAM, empty slots and speed below the module rating.</summary>
    public static List<string> MemoryHints()
    {
        var hints = new List<string>();
        var (total, avail, load) = SystemInfo.MemoryStatus();
        var mods = SystemInfo.MemoryModules();
        long installed = mods.Sum(m => m.CapacityBytes);
        if (installed == 0) installed = (long)SystemInfo.InstalledMemoryBytes();

        hints.Add($"RAM-Nutzung: {SystemInfo.Gb(total - avail)} belegt, {SystemInfo.Gb(avail)} ungenutzt ({100 - load} % frei) von {SystemInfo.Gb(total)} nutzbar.");
        if (installed > 0 && installed - (long)total > (512L << 20))
            hints.Add($"⚠ {SystemInfo.Gb(installed - (long)total)} des installierten RAMs ({SystemInfo.Gb(installed)}) sind für Windows nicht nutzbar (Hardware-reserviert, z. B. integrierte Grafik).");

        int slots = SystemInfo.MemorySlots();
        if (slots > 0 && mods.Count > 0 && mods.Count < slots)
            hints.Add($"Steckplätze: {mods.Count} von {slots} belegt – {slots - mods.Count} frei.");
        if (mods.Count == 1 && mods[0].FormFactor is 8 or 12)   // only for socketed modules
            hints.Add("⚠ Nur ein RAM-Modul: läuft im Single-Channel-Betrieb (halbe theoretische Bandbreite gegenüber Dual-Channel).");

        foreach (var m in mods)
        {
            if (m.SpeedMts > 0 && m.ConfiguredMts > 0 && m.ConfiguredMts < m.SpeedMts)
                hints.Add($"⚠ {m.Slot}: läuft mit {m.ConfiguredMts} MT/s, das Modul meldet {m.SpeedMts} MT/s.");
            int rated = m.PartNumberMts;
            if (rated > 0 && m.ConfiguredMts > 0 && m.ConfiguredMts < rated)
                hints.Add($"⚠ {m.Slot} ({m.PartNumber.Trim()}): Die Teilenummer weist auf {rated} MT/s hin, konfiguriert sind {m.ConfiguredMts} MT/s – XMP/EXPO-Profil im BIOS prüfen.");
        }
        return hints;
    }

    /// <summary>Runs passFn(threadIndex) on all threads repeatedly for RoundSeconds; best of Rounds in MB/s (10^6).</summary>
    private static double Bandwidth(int threads, CancellationToken ct, Func<int, long> passFn, Action<double> report)
    {
        double best = 0;
        for (int r = 0; r < Rounds; r++)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = new long[threads];
            var t0 = new long[threads]; var t1 = new long[threads];
            int stop = 0;
            using var barrier = new Barrier(threads + 1);
            Exception? err = null;
            var ths = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                barrier.SignalAndWait();
                t0[t] = Stopwatch.GetTimestamp();      // each thread times its own work
                long sum = 0;
                try { while (Volatile.Read(ref stop) == 0) sum += passFn(t); }
                catch (Exception ex) { err = ex; }       // never let a worker thread take the whole program down
                t1[t] = Stopwatch.GetTimestamp();
                bytes[t] = sum;
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToArray();
            foreach (var th in ths) th.Start();
            barrier.SignalAndWait();
            Thread.Sleep(TimeSpan.FromSeconds(RoundSeconds));
            Volatile.Write(ref stop, 1);
            foreach (var th in ths) th.Join();
            if (err != null) throw err;
            // wall span from the first thread starting to the last thread finishing (never shorter than the real work)
            double sec = (t1.Max() - t0.Min()) / (double)Stopwatch.Frequency;
            best = Math.Max(best, bytes.Sum() / sec / 1e6);
            report((r + 1) / (double)Rounds);
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void ReadSlice(byte* p, long len)
    {
        if (!Vector256.IsHardwareAccelerated) { ReadSlice128(p, len); return; }
        var a0 = Vector256<long>.Zero; var a1 = a0; var a2 = a0; var a3 = a0;
        byte* end = p + len;
        for (; p < end; p += 128)
        {
            a0 += Vector256.LoadAligned((long*)p);
            a1 += Vector256.LoadAligned((long*)(p + 32));
            a2 += Vector256.LoadAligned((long*)(p + 64));
            a3 += Vector256.LoadAligned((long*)(p + 96));
        }
        Interlocked.Add(ref _sink, Vector256.Sum(a0 + a1 + a2 + a3));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void ReadSlice128(byte* p, long len)
    {
        var a0 = Vector128<long>.Zero; var a1 = a0; var a2 = a0; var a3 = a0;
        byte* end = p + len;
        for (; p < end; p += 64)
        {
            a0 += Vector128.LoadAligned((long*)p); a1 += Vector128.LoadAligned((long*)(p + 16));
            a2 += Vector128.LoadAligned((long*)(p + 32)); a3 += Vector128.LoadAligned((long*)(p + 48));
        }
        Interlocked.Add(ref _sink, Vector128.Sum(a0 + a1 + a2 + a3));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void WriteSlice(byte* p, long len)
    {
        byte* end = p + len;
        if (Avx.IsSupported)
        {
            var v = Vector256.Create((byte)0x3C);
            for (; p < end; p += 128)
            {
                Avx.StoreAlignedNonTemporal(p, v); Avx.StoreAlignedNonTemporal(p + 32, v);
                Avx.StoreAlignedNonTemporal(p + 64, v); Avx.StoreAlignedNonTemporal(p + 96, v);
            }
            Sse.StoreFence();
        }
        else
        {
            var v = Vector128.Create((byte)0x3C);
            for (; p < end; p += 64)
            {
                Vector128.StoreAligned(v, p); Vector128.StoreAligned(v, p + 16);
                Vector128.StoreAligned(v, p + 32); Vector128.StoreAligned(v, p + 48);
            }
        }
    }

    /// <summary>Random cyclic pointer chain over all cache lines of the buffer; returns ns per dependent load.</summary>
    private static double Latency(byte* buf, long size, CancellationToken ct, Action<double> report)
    {
        int lines = (int)(size / 64);
        var perm = new int[lines];
        for (int i = 0; i < lines; i++) perm[i] = i;
        var rng = new Random(12345);
        for (int i = lines - 1; i > 0; i--)                       // Sattolo: single cycle through all lines
        {
            int j = rng.Next(i);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        for (int i = 0; i < lines; i++) *(byte**)(buf + (long)i * 64) = buf + (long)perm[i] * 64;
        perm = null!;

        double best = double.MaxValue;
        const int steps = 4_000_000;
        nint baseAddr = (nint)buf;
        Exception? err = null;
        var th = new Thread(() =>
        {
          try
          {
            CpuTopology.PinCurrentThreadToPerformanceCores();
            byte* p = (byte*)baseAddr;
            p = Chase(p, 200_000);                                // warm up TLB / prefetchers
            for (int r = 0; r < 4; r++)
            {
                if (ct.IsCancellationRequested) return;
                long t0 = Stopwatch.GetTimestamp();
                p = Chase(p, steps);
                long t1 = Stopwatch.GetTimestamp();
                best = Math.Min(best, (t1 - t0) * 1e9 / Stopwatch.Frequency / steps);
                report((r + 1) / 4.0);
            }
            Interlocked.Add(ref _sink, (long)p);
          }
          catch (Exception ex) { err = ex; }
        }) { IsBackground = true, Priority = ThreadPriority.Highest };
        th.Start();
        th.Join();
        if (err != null) throw err;
        ct.ThrowIfCancellationRequested();
        return best;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static byte* Chase(byte* p, int steps)
    {
        for (int i = 0; i < steps; i += 8)
        {
            p = *(byte**)p; p = *(byte**)p; p = *(byte**)p; p = *(byte**)p;
            p = *(byte**)p; p = *(byte**)p; p = *(byte**)p; p = *(byte**)p;
        }
        return p;
    }
}
