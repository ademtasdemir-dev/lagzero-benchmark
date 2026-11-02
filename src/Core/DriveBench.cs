using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lagzero.Core;

public sealed record DriveTarget(
    string Root, string VolumeLabel, string FileSystem, long FreeBytes, long TotalBytes,
    bool IsSystem, string Kind, string Model, string Bus, int DiskNumber = -1, int BusId = 0, int MediaId = 0)
{
    public string Letter => Root.TrimEnd('\\', ':');
    public override string ToString() =>
        $"{Root} · {Kind} · {Model} · {SystemInfo.Gb(FreeBytes)} frei von {SystemInfo.Gb(TotalBytes)}" +
        (string.IsNullOrEmpty(VolumeLabel) ? "" : $" · „{VolumeLabel}“") + (IsSystem ? "  [SYSTEMLAUFWERK]" : "");
}

public static class DriveCatalog
{
    public const string TestFolderName = "LAGZERO_Benchmark_Temp";

    public static List<DriveTarget> Enumerate()
    {
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        // drive letter -> physical disk number -> model / media / bus
        var letterToDisk = new Dictionary<char, int>();
        foreach (var p in SystemInfo.Wmi("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
        {
            var dl = SystemInfo.S(p, "DriveLetter");
            if (dl.Length == 1 && char.IsLetter(dl[0])) letterToDisk[char.ToUpperInvariant(dl[0])] = (int)SystemInfo.L(p, "DiskNumber");
        }
        var disks = new Dictionary<int, (string Model, int Media, int Bus)>();
        foreach (var d in SystemInfo.Wmi("SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            if (int.TryParse(SystemInfo.S(d, "DeviceId"), out int id))
                disks[id] = (SystemInfo.S(d, "FriendlyName"), (int)SystemInfo.L(d, "MediaType"), (int)SystemInfo.L(d, "BusType"));

        var list = new List<DriveTarget>();
        foreach (var di in DriveInfo.GetDrives())
        {
            try
            {
                if (!di.IsReady || di.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                char letter = char.ToUpperInvariant(di.Name[0]);
                string model = "unbekannt", bus = "unbekannt"; int media = 0, busId = 0;
                if (letterToDisk.TryGetValue(letter, out int dn) && disks.TryGetValue(dn, out var info))
                {
                    model = info.Model; media = info.Media; busId = info.Bus;
                    bus = SystemInfo.BusTypeName(info.Bus);
                }
                string kind = busId == 7
                    ? (di.DriveType == DriveType.Removable ? "USB-Stick / Wechseldatenträger" : $"Externe {(media == 3 ? "Festplatte (HDD)" : media == 4 ? "SSD" : "Platte")} (USB)")
                    : media switch { 3 => $"HDD ({bus})", 4 => $"SSD ({bus})", _ => di.DriveType == DriveType.Removable ? "Wechseldatenträger" : $"Laufwerk ({bus})" };
                list.Add(new DriveTarget(di.Name, di.VolumeLabel, di.DriveFormat, di.AvailableFreeSpace, di.TotalSize,
                    string.Equals(di.Name, sysRoot, StringComparison.OrdinalIgnoreCase), kind, model, bus,
                    letterToDisk.TryGetValue(letter, out int dnum) ? dnum : -1, busId, media));
            }
            catch { /* drive vanished */ }
        }
        return list;
    }
}

/// <summary>Wraps aligned native memory as Memory&lt;byte&gt; for unbuffered async I/O.</summary>
internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private readonly byte* _p;
    private readonly int _len;
    public AlignedBuffer(int len, int align = 4096) { _len = len; _p = (byte*)NativeMemory.AlignedAlloc((nuint)len, (nuint)align); }
    public byte* Pointer => _p;
    public override Span<byte> GetSpan() => new(_p, _len);
    public override MemoryHandle Pin(int elementIndex = 0) => new(_p + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(_p);
}

/// <summary>
/// Sequential (1 MiB, QD8) and 4K random (QD1 / QD32) read/write with unbuffered I/O (FILE_FLAG_NO_BUFFERING + WRITE_THROUGH).
/// Works only on its own test file inside &lt;drive&gt;\LAGZERO_Benchmark_Temp\ (CreateNew – never overwrites anything),
/// the file is opened DeleteOnClose and removed afterwards.
/// </summary>
public sealed class DriveBenchmark : IBenchmark
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int SeqBlock = 1 << 20, SeqQd = 8, RndBlock = 4096;

    private readonly DriveTarget _t;
    private readonly long _size;
    private readonly bool _allowSystem;
    private readonly double _rndSeconds;

    public DriveBenchmark(DriveTarget target, long sizeBytes, bool allowSystemDrive, double randomSeconds = 4)
    {
        _t = target; _size = sizeBytes / SeqBlock * SeqBlock; _allowSystem = allowSystemDrive; _rndSeconds = randomSeconds;
    }

    /// <summary>Must be set explicitly after the user confirmed the write test – otherwise nothing is written.</summary>
    public bool Confirmed { get; init; }

    public string Id => $"drive.{_t.Letter}";
    public string Title => $"Laufwerk {_t.Letter}:";

    public static string TestFolder(DriveTarget t) => Path.Combine(t.Root, DriveCatalog.TestFolderName);

    public Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct) => Task.Run(() => Run(progress, ct), ct);

    private async Task<BenchResult> Run(IProgress<BenchProgress> progress, CancellationToken ct)
    {
        if (!Confirmed)
            throw new InvalidOperationException("Schreibtest nicht bestätigt – nichts geschrieben.");
        if (_t.IsSystem && !_allowSystem)
            throw new InvalidOperationException("Systemlaufwerk ist nicht ausdrücklich freigegeben.");
        ct.ThrowIfCancellationRequested();   // never create anything once a cancel / shutdown is pending
        var fresh = new DriveInfo(_t.Root);
        if (fresh.AvailableFreeSpace < _size + Math.Max(1L << 30, fresh.TotalSize / 50))
            throw new InvalidOperationException($"Zu wenig freier Speicher auf {_t.Root} für {SystemInfo.Gb(_size)} Testdatei (+ Reserve).");

        var res = new BenchResult { TestId = Id, Title = Title, StartedAt = DateTime.Now };
        var total = Stopwatch.StartNew();
        string folder = TestFolder(_t);
        bool createdFolder = false;
        string file = Path.Combine(folder, $"lzbench_{Guid.NewGuid():N}.tmp");
        var series = new List<double>();

        try
        {
            ct.ThrowIfCancellationRequested();
            createdFolder = !Directory.Exists(folder);
            Directory.CreateDirectory(folder);
            using var h = File.OpenHandle(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose | NoBuffering);

            using var seqBuf = new AlignedBuffer(SeqBlock * SeqQd);
            Random.Shared.NextBytes(seqBuf.GetSpan());         // incompressible data

            // 1) sequential write (also creates the file content)
            progress.Report(new BenchProgress(0, "Sequenziell schreiben …"));
            double seqW = await Sequential(h, seqBuf, write: true, ct, f => progress.Report(new BenchProgress(0.25 * f, $"Sequenziell schreiben · {f:P0}")), series);
            res.Add("drive.seq.write", "Schreiben", seqW, "MB/s", true, "N1", "Sequenziell 1 MiB · QD8");

            // 2) sequential read
            double seqR = await Sequential(h, seqBuf, write: false, ct, f => progress.Report(new BenchProgress(0.25 + 0.2 * f, $"Sequenziell lesen · {f:P0}")), null);
            res.Metrics.Insert(0, new Metric("drive.seq.read", "Lesen", seqR, "MB/s", true, "N1", "Sequenziell 1 MiB · QD8"));

            // 3–6) 4K random
            using var rndBuf = new AlignedBuffer(RndBlock * 32);
            Random.Shared.NextBytes(rndBuf.GetSpan());
            var steps = new (bool write, int qd, double from)[] { (false, 1, 0.45), (true, 1, 0.59), (false, 32, 0.73), (true, 32, 0.86) };
            foreach (var (write, qd, from) in steps)
            {
                string name = $"4K zufällig {(write ? "schreiben" : "lesen")} QD{qd}";
                var (mbps, iops, latUs) = await Random4K(h, rndBuf, write, qd, ct,
                    f => progress.Report(new BenchProgress(from + 0.14 * f, $"{name} · {f:P0}")));
                string grp = $"4K zufällig · QD{qd}";
                string op = write ? "Schreiben" : "Lesen";
                res.Add($"drive.4k.q{qd}.{(write ? "write" : "read")}", op, mbps, "MB/s", true, "N2", grp);
                res.Add($"drive.4k.q{qd}.{(write ? "write" : "read")}.iops", $"{op} · IOPS", iops, "IOPS", true, "N0", grp);
                res.Add($"drive.4k.q{qd}.{(write ? "write" : "read")}.lat", $"{op} · Ø Latenz", latUs, "µs", false, "N0", grp);
            }
        }
        catch (OperationCanceledException) { res.Cancelled = true; }
        finally
        {
            // DeleteOnClose already removed the file when the handle closed; this is a safety net for our own file only.
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            try { if (createdFolder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch { }
        }

        res.DurationSec = total.Elapsed.TotalSeconds;
        var iface = _t.DiskNumber >= 0 ? DiskInterfaces.ForDisk(_t.DiskNumber, _t.BusId, _t.MediaId) : DiskInterface.Unknown;
        res.Info["interface_class"] = iface.Kind;
        res.Info["interface"] = iface.ClassName;
        if (iface.Detail.Length > 0) res.Info["interface_detail"] = iface.Detail;
        if (iface.DeviceNote != null) res.Info["interface_note"] = iface.DeviceNote;
        res.Info["drive_kind"] = _t.Kind;
        res.Info["drive_model"] = _t.Model;
        res.Series = series.Count > 1 ? series.ToArray() : null;
        res.SeriesLabel = "Sequenzielles Schreiben je 64-MiB-Abschnitt (MB/s)";
        res.Notes.Add($"{_t.Kind} · {_t.Model} · {_t.FileSystem} · Testdatei {SystemInfo.Gb(_size)} in {folder}\\ (danach gelöscht" +
                      (createdFolder ? ", Ordner ebenfalls entfernt)." : ")."));
        res.Notes.Add($"Schnittstelle: {iface.ClassName}{(iface.Detail.Length > 0 ? " (" + iface.Detail + ")" : "")}." + (iface.DeviceNote != null ? " " + iface.DeviceNote : ""));
        res.Notes.Add($"Ungepuffertes I/O (FILE_FLAG_NO_BUFFERING + WRITE_THROUGH), 1 MB = 10^6 Byte. 4K-Tests je {_rndSeconds:0} s, gleichverteilte 4-KiB-Offsets in der Testdatei.");
        progress.Report(new BenchProgress(res.Cancelled ? 0 : 1, res.Cancelled ? "Abgebrochen" : "Fertig"));
        return res;
    }

    /// <summary>Whole-file sequential pass with QD8 (8 outstanding 1 MiB requests). Returns MB/s (10^6).</summary>
    private async Task<double> Sequential(SafeFileHandle h, AlignedBuffer buf, bool write, CancellationToken ct, Action<double> report, List<double>? series)
    {
        long blocks = _size / SeqBlock;
        long next = -1, done = 0;
        const long sectionBlocks = 64;
        long sectionStart = Stopwatch.GetTimestamp();
        var sectionLock = new object();
        var sw = Stopwatch.StartNew();

        async Task Worker(int slot)
        {
            var mem = buf.Memory.Slice(slot * SeqBlock, SeqBlock);
            long b;
            while ((b = Interlocked.Increment(ref next)) < blocks)
            {
                ct.ThrowIfCancellationRequested();
                if (write) await RandomAccess.WriteAsync(h, (ReadOnlyMemory<byte>)mem, b * SeqBlock, ct);
                else
                {
                    int n = await RandomAccess.ReadAsync(h, mem, b * SeqBlock, ct);
                    if (n != SeqBlock) throw new IOException("Unerwartet kurzer Lesevorgang.");
                }
                long d = Interlocked.Increment(ref done);
                if (d % 16 == 0) report(d / (double)blocks);
                if (series != null && d % sectionBlocks == 0)
                    lock (sectionLock)
                    {
                        long now = Stopwatch.GetTimestamp();
                        series.Add(sectionBlocks * SeqBlock / ((now - sectionStart) / (double)Stopwatch.Frequency) / 1e6);
                        sectionStart = now;
                    }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, SeqQd).Select(Worker));
        double sec = sw.Elapsed.TotalSeconds;
        report(1);
        return _size / sec / 1e6;
    }

    /// <summary>Random 4 KiB requests for a fixed time with the given queue depth. Returns (MB/s, IOPS, avg latency µs).</summary>
    private async Task<(double, double, double)> Random4K(SafeFileHandle h, AlignedBuffer buf, bool write, int qd, CancellationToken ct, Action<double> report)
    {
        long slots = _size / RndBlock;
        long ops = 0, latTicks = 0;
        long end = Stopwatch.GetTimestamp() + (long)(_rndSeconds * Stopwatch.Frequency);
        var sw = Stopwatch.StartNew();

        if (qd == 1)
        {
            // synchronous: one request in flight, exact per-request latency
            await Task.Run(() =>
            {
                var rng = new Random(1);
                var span = buf.GetSpan()[..RndBlock];
                long lastReport = 0;
                while (Stopwatch.GetTimestamp() < end)
                {
                    ct.ThrowIfCancellationRequested();
                    long off = rng.NextInt64(slots) * RndBlock;
                    long t0 = Stopwatch.GetTimestamp();
                    if (write) RandomAccess.Write(h, (ReadOnlySpan<byte>)span, off); else RandomAccess.Read(h, span, off);
                    long t1 = Stopwatch.GetTimestamp();
                    latTicks += t1 - t0; ops++;
                    if (t1 - lastReport > Stopwatch.Frequency / 10) { lastReport = t1; report(sw.Elapsed.TotalSeconds / _rndSeconds); }
                }
            }, ct);
        }
        else
        {
            async Task Worker(int slot)
            {
                var rng = new Random(slot + 7);
                var mem = buf.Memory.Slice(slot * RndBlock, RndBlock);
                while (Stopwatch.GetTimestamp() < end)
                {
                    ct.ThrowIfCancellationRequested();
                    long off = rng.NextInt64(slots) * RndBlock;
                    long t0 = Stopwatch.GetTimestamp();
                    if (write) await RandomAccess.WriteAsync(h, (ReadOnlyMemory<byte>)mem, off, ct);
                    else await RandomAccess.ReadAsync(h, mem, off, ct);
                    Interlocked.Add(ref latTicks, Stopwatch.GetTimestamp() - t0);
                    if (Interlocked.Increment(ref ops) % 256 == 0) report(sw.Elapsed.TotalSeconds / _rndSeconds);
                }
            }
            await Task.WhenAll(Enumerable.Range(0, qd).Select(Worker));
        }
        double sec = sw.Elapsed.TotalSeconds;
        report(1);
        double iops = ops / sec;
        return (iops * RndBlock / 1e6, iops, ops > 0 ? latTicks * 1e6 / Stopwatch.Frequency / ops : 0);
    }
}
