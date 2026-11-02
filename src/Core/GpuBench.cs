using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Lagzero.Core;

/// <summary>Own test window for the render tests (runs on its own STA thread with its own message loop).</summary>
internal sealed class RenderWindow : Form
{
    public volatile bool UserClosed;

    public RenderWindow(string title, int width, int height)
    {
        Text = title;
        BackColor = System.Drawing.Color.Black;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(width, height);
        KeyPreview = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* swap chain owns the client area */ }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }
    protected override void OnFormClosing(FormClosingEventArgs e) { UserClosed = true; base.OnFormClosing(e); }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public float Time;
    public uint Grid;
    public uint Layers;
    public uint PsIters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ComputeConstants
{
    public uint Iters;
    public float Seed;
    public uint Pad0, Pad1;
}

/// <summary>Minimal Direct3D 11 renderer: instanced lit cubes with a fixed per-pixel ALU load, plus an FMA compute kernel.</summary>
internal sealed class D3DRenderer : IDisposable
{
    public readonly int RenderWidth, RenderHeight;

    private const string SceneHlsl = """
        cbuffer Frame : register(b0) { row_major float4x4 ViewProj; float Time; uint Grid; uint Layers; uint PsIters; };
        struct VSIn  { float3 pos : POSITION; float3 nrm : NORMAL; uint iid : SV_InstanceID; };
        struct VSOut { float4 pos : SV_Position; float3 wpos : TEXCOORD0; float3 nrm : TEXCOORD1; float3 col : TEXCOORD2; };
        float hash(uint n) { n = (n << 13U) ^ n; n = n * (n * n * 15731U + 789221U) + 1376312589U; return float(n & 0x7fffffffU) / 2147483647.0; }
        float3 rot(float3 p, float a, float b) {
            float sa = sin(a), ca = cos(a), sb = sin(b), cb = cos(b);
            p = float3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z);
            return float3(p.x, cb * p.y - sb * p.z, sb * p.y + cb * p.z);
        }
        VSOut VSMain(VSIn i) {
            uint x = i.iid % Grid, z = (i.iid / Grid) % Grid, y = i.iid / (Grid * Grid);
            float h = hash(i.iid);
            float3 c = float3((float)x - Grid * 0.5, (float)y * 2.2 - Layers * 1.1, (float)z - Grid * 0.5) * 2.4;
            c.y += sin(Time * 1.3 + h * 6.28) * 0.6;
            float a = Time * (0.4 + h) + h * 10.0, b = Time * (0.3 + h * 0.5);
            VSOut o;
            float3 w = rot(i.pos * 0.85, a, b) + c;
            o.wpos = w; o.nrm = rot(i.nrm, a, b);
            o.col = h > 0.86 ? float3(0.88, 0.05, 0.02) : lerp(float3(0.45, 0.47, 0.5), float3(0.85, 0.87, 0.9), h);
            o.pos = mul(float4(w, 1.0), ViewProj);
            return o;
        }
        float4 PSMain(VSOut v) : SV_Target {
            float3 n = normalize(v.nrm);
            float3 l1 = normalize(float3(0.4, 0.9, -0.3));
            float3 l2p = float3(sin(Time * 0.7) * 40.0, 10.0, cos(Time * 0.7) * 40.0);
            float3 l2 = normalize(l2p - v.wpos);
            float att = 1.0 / (1.0 + dot(l2p - v.wpos, l2p - v.wpos) * 0.0008);
            float3 col = v.col * (0.12 + 0.75 * saturate(dot(n, l1))) + float3(1.0, 0.08, 0.05) * saturate(dot(n, l2)) * att;
            float acc = 0; float3 p = v.wpos * 0.37 + Time * 0.1;
            [loop] for (uint k = 0; k < PsIters; k++) { p = p.yzx * 1.013 + float3(0.11, 0.07, 0.05); acc += sin(p.x) * cos(p.y) + sin(p.z * 1.7); }
            col += 0.03 * saturate(abs(acc) / max(1.0, (float)PsIters)) * float3(1.0, 0.2, 0.1);
            float fog = saturate(length(v.wpos) / 160.0);
            return float4(lerp(col, float3(0.02, 0.02, 0.025), fog * fog), 1.0);
        }
        """;

    private const string ComputeHlsl = """
        cbuffer C : register(b0) { uint Iters; float Seed; uint2 Pad; };
        RWStructuredBuffer<float> Out : register(u0);
        [numthreads(256, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID) {
            float s = (float)id.x * 1e-7 + Seed;
            float4 a0 = float4(s, s + 0.1, s + 0.2, s + 0.3), a1 = a0 + 0.01, a2 = a0 + 0.02, a3 = a0 + 0.03;
            float4 a4 = a0 + 0.04, a5 = a0 + 0.05, a6 = a0 + 0.06, a7 = a0 + 0.07;
            float4 m = float4(0.9999, 0.99991, 0.99992, 0.99993);
            float4 k = 0.0001 + Seed * 1e-6;
            [loop] for (uint i = 0; i < Iters; i++) {
                [unroll] for (int u = 0; u < 4; u++) {
                    a0 = mad(a0, m, k); a1 = mad(a1, m, k); a2 = mad(a2, m, k); a3 = mad(a3, m, k);
                    a4 = mad(a4, m, k); a5 = mad(a5, m, k); a6 = mad(a6, m, k); a7 = mad(a7, m, k);
                }
            }
            float4 r = a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7;
            Out[id.x] = r.x + r.y + r.z + r.w;
        }
        """;
    /// <summary>FLOPs per compute thread and loop iteration: 4 unrolled × 8 vectors × 4 lanes × 2 (FMA).</summary>
    public const double ComputeFlopsPerIter = 4 * 8 * 4 * 2;

    private readonly IDXGIFactory6? _factory;
    private readonly IDXGIFactory2? _factory2;
    private IDXGIFactory2 Factory => (IDXGIFactory2?)_factory ?? _factory2!;
    private readonly IDXGIAdapter1 _adapter;
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Ctx;
    private readonly IDXGISwapChain1 _swap;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11Texture2D _depth;
    private readonly ID3D11DepthStencilView _dsv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _vb, _cb;
    private readonly ID3D11RasterizerState _raster;
    public readonly bool Tearing;
    public readonly string AdapterName;
    public readonly ulong VramBytes;
    public readonly FeatureLevel Level;

    public D3DRenderer(IntPtr hwnd, int width, int height)
    {
        RenderWidth = width; RenderHeight = height;
        // IDXGIFactory6 (GPU preference) needs Windows 10 1803+; older systems fall back to the first adapter
        if (DXGI.CreateDXGIFactory2(false, out IDXGIFactory6? f).Success && f != null)
        {
            _factory = f;
            _adapter = _factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.HighPerformance);
        }
        else
        {
            DXGI.CreateDXGIFactory2(false, out IDXGIFactory2? f2).CheckError();
            _factory2 = f2!;
            _factory2.EnumAdapters1(0, out IDXGIAdapter1? a).CheckError();
            _adapter = a!;
        }
        var ad = _adapter.Description1;
        AdapterName = ad.Description.Trim();
        VramBytes = (ulong)ad.DedicatedVideoMemory;

        D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            Environment.GetEnvironmentVariable("LAGZERO_FL110") == "1" ? new[] { FeatureLevel.Level_11_0 } : new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device? dev, out FeatureLevel level, out ID3D11DeviceContext? ctx).CheckError();
        Device = dev!; Ctx = ctx!; Level = level;

        try { Tearing = _factory?.PresentAllowTearing ?? false; } catch { Tearing = false; }
        var desc = new SwapChainDescription1
        {
            Width = (uint)RenderWidth, Height = (uint)RenderHeight, Format = Format.R8G8B8A8_UNorm, BufferCount = 3,
            BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
            Flags = Tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };
        _swap = Factory.CreateSwapChainForHwnd(Device, hwnd, desc);
        Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        using (var dxgiDev = Device.QueryInterface<IDXGIDevice1>()) dxgiDev.SetMaximumFrameLatency(1); // keep CPU in lockstep: frame time = real frame time

        using (var back = _swap.GetBuffer<ID3D11Texture2D>(0)) _rtv = Device.CreateRenderTargetView(back);
        _depth = Device.CreateTexture2D(Format.D32_Float, (uint)RenderWidth, (uint)RenderHeight, 1, 1, null, BindFlags.DepthStencil);
        _dsv = Device.CreateDepthStencilView(_depth);

        var vsCode = Compiler.Compile(SceneHlsl, "VSMain", "scene.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3);
        var psCode = Compiler.Compile(SceneHlsl, "PSMain", "scene.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3);
        _vs = Device.CreateVertexShader(vsCode.Span);
        _ps = Device.CreatePixelShader(psCode.Span);
        _layout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        }, vsCode.Span);
        _vb = Device.CreateBuffer(CubeVertices(), BindFlags.VertexBuffer);
        _cb = Device.CreateBuffer((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer);
        _raster = Device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    private static float[] CubeVertices()
    {
        var v = new List<float>();
        void Face(Vector3 n, Vector3 u, Vector3 w)
        {
            var c = n; Vector3 p0 = c - u - w, p1 = c + u - w, p2 = c + u + w, p3 = c - u + w;
            foreach (var p in new[] { p0, p1, p2, p0, p2, p3 }) { v.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z }); }
        }
        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ); Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX); Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY); Face(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        return v.ToArray();
    }

    /// <summary>Renders one frame of the scene (does not present).</summary>
    public void RenderScene(float time, uint grid, uint layers, uint psIters)
    {
        float r = grid * 1.55f;
        var eye = new Vector3(MathF.Sin(time * 0.15f) * r, layers * 2.2f + 10f, MathF.Cos(time * 0.15f) * r);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0, -2, 0), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, RenderWidth / (float)RenderHeight, 0.5f, 600f);
        var cb = new FrameConstants { ViewProj = view * proj, Time = time, Grid = grid, Layers = layers, PsIters = psIters };
        Ctx.UpdateSubresource(ref cb, _cb);

        Ctx.OMSetRenderTargets(_rtv, _dsv);
        Ctx.RSSetViewport(0, 0, RenderWidth, RenderHeight);
        Ctx.RSSetState(_raster);
        Ctx.ClearRenderTargetView(_rtv, new Color4(0.02f, 0.02f, 0.025f, 1f));
        Ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1f, 0);
        Ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Ctx.IASetInputLayout(_layout);
        Ctx.IASetVertexBuffer(0, _vb, 24);
        Ctx.VSSetShader(_vs); Ctx.VSSetConstantBuffer(0, _cb);
        Ctx.PSSetShader(_ps); Ctx.PSSetConstantBuffer(0, _cb);
        Ctx.DrawInstanced(36, grid * grid * layers, 0, 0);
    }

    public void Present(bool vsync) =>
        _swap.Present(vsync ? 1u : 0u, !vsync && Tearing ? PresentFlags.AllowTearing : PresentFlags.None);

    /// <summary>FMA compute throughput in GFLOPS, timed with GPU timestamp queries.</summary>
    public double ComputeGflops(double seconds, CancellationToken ct, Func<bool> abort, Action<double> report, out double gpuSeconds)
    {
        var csCode = Compiler.Compile(ComputeHlsl, "CSMain", "compute.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
        using var cs = Device.CreateComputeShader(csCode.Span);
        const uint maxGroups = 65535;
        using var outBuf = Device.CreateBuffer(maxGroups * 256 * 4, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured, 4);
        using var uav = Device.CreateUnorderedAccessView(outBuf);
        using var cb = Device.CreateBuffer((uint)Marshal.SizeOf<ComputeConstants>(), BindFlags.ConstantBuffer);
        using var disjoint = Device.CreateQuery(QueryType.TimestampDisjoint);
        using var t0 = Device.CreateQuery(QueryType.Timestamp);
        using var t1 = Device.CreateQuery(QueryType.Timestamp);
        const uint iters = 256;
        var c = new ComputeConstants { Iters = iters, Seed = 0.37f };
        Ctx.UpdateSubresource(ref c, cb);
        Ctx.CSSetShader(cs); Ctx.CSSetConstantBuffer(0, cb); Ctx.CSSetUnorderedAccessView(0, uav);

        double Batch(uint groups, int dispatches)
        {
            Ctx.Begin(disjoint);
            Ctx.End(t0);
            for (int i = 0; i < dispatches; i++) Ctx.Dispatch(groups, 1, 1);
            Ctx.End(t1);
            Ctx.End(disjoint);
            QueryDataTimestampDisjoint dj;
            while (!Ctx.GetData(disjoint, AsyncGetDataFlags.None, out dj)) { if (ct.IsCancellationRequested || abort()) throw new OperationCanceledException(); Thread.Yield(); }
            ulong a, b;
            while (!Ctx.GetData(t0, AsyncGetDataFlags.None, out a)) Thread.Yield();
            while (!Ctx.GetData(t1, AsyncGetDataFlags.None, out b)) Thread.Yield();
            if (dj.Disjoint || dj.Frequency == 0) return double.NaN;
            return (b - a) / (double)dj.Frequency;
        }

        // calibrate: one dispatch should take ~8 ms (safe against driver timeouts on slow GPUs)
        uint groups = 64;
        double tCal = Batch(groups, 1);
        for (int k = 0; k < 6 && (double.IsNaN(tCal) || tCal < 0.002) && groups < maxGroups; k++) { groups = Math.Min(maxGroups, groups * 4); tCal = Batch(groups, 1); }
        if (!double.IsNaN(tCal) && tCal > 0) groups = (uint)Math.Clamp(groups * (0.008 / tCal), 64, maxGroups);

        double flopsPerDispatch = groups * 256.0 * iters * ComputeFlopsPerIter;
        double totalFlops = 0, totalTime = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (ct.IsCancellationRequested || abort()) throw new OperationCanceledException();
            double t = Batch(groups, 10);
            if (double.IsNaN(t) || t <= 0) continue;
            totalFlops += flopsPerDispatch * 10; totalTime += t;
            report(sw.Elapsed.TotalSeconds / seconds);
            Application.DoEvents();
        }
        gpuSeconds = totalTime;
        return totalTime > 0 ? totalFlops / totalTime / 1e9 : 0;
    }

    public void Dispose()
    {
        Ctx.ClearState(); Ctx.Flush();
        _raster.Dispose(); _cb.Dispose(); _vb.Dispose(); _layout.Dispose(); _ps.Dispose(); _vs.Dispose();
        _dsv.Dispose(); _depth.Dispose(); _rtv.Dispose(); _swap.Dispose();
        Ctx.Dispose(); Device.Dispose(); _adapter.Dispose(); _factory?.Dispose(); _factory2?.Dispose();
    }
}

/// <summary>Frame time statistics shared by the GPU and frame time tests.</summary>
public static class FrameStats
{
    public sealed record Result(int Frames, double AvgFps, double Low1Fps, double Low01Fps, double AvgMs, double MedianMs, double P99Ms, double MaxMs, int Stutters, double StutterThresholdMs);

    /// <summary>
    /// 1 % / 0,1 % Low = 1000 / mean of the slowest 1 % / 0,1 % frame times.
    /// Stutter ("Ruckler") = frame time above 2 × median AND at least 4 ms above the median (so sub-millisecond jitter at very high FPS is not counted).
    /// </summary>
    public static Result Compute(double[] ms)
    {
        if (ms.Length == 0) return new Result(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var s = ms.OrderBy(x => x).ToArray();
        double sum = ms.Sum();
        double LowFps(double frac)
        {
            int n = Math.Max(1, (int)Math.Ceiling(s.Length * frac));
            return 1000.0 / s.Skip(s.Length - n).Average();
        }
        double med = s[s.Length / 2];
        double thr = Math.Max(med * 2, med + 4.0);
        return new Result(ms.Length, ms.Length / (sum / 1000.0), LowFps(0.01), LowFps(0.001), sum / ms.Length, med,
            s[Math.Clamp((int)Math.Ceiling(0.99 * s.Length) - 1, 0, s.Length - 1)], s[^1], ms.Count(x => x > thr), thr);
    }
}

/// <summary>Runs a render benchmark body on its own STA thread with its own test window.</summary>
internal static class RenderHost
{
    /// <summary>Window client area: the render resolution scaled down to fit ~75 % of the work area (swap chain keeps the full resolution).</summary>
    private static System.Drawing.Size WindowSize(int w, int h)
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        double f = Math.Min(1.0, Math.Min(wa.Width * 0.75 / w, wa.Height * 0.75 / h));
        return new System.Drawing.Size((int)(w * f), (int)(h * f));
    }

    public static Task<BenchResult> Run(string title, int width, int height, Func<RenderWindow, D3DRenderer, BenchResult> body)
    {
        var tcs = new TaskCompletionSource<BenchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try
            {
                var ws = WindowSize(width, height);
                using var win = new RenderWindow($"{title} · {width}×{height}", ws.Width, ws.Height);
                win.Show();
                win.Activate();
                Application.DoEvents();
                using var r = new D3DRenderer(win.Handle, width, height);
                var res = body(win, r);
                if (!win.IsDisposed) win.Close();
                tcs.SetResult(res);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "LAGZERO render" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }

    /// <summary>Renders frames for warmup + measure seconds; returns frame times (ms) of the measured part.</summary>
    public static double[] RenderLoop(RenderWindow win, D3DRenderer r, double warmup, double measure, uint grid, uint layers, uint psIters,
        bool vsync, CancellationToken ct, Action<double, double> report)
    {
        var times = new List<double>(100_000);
        var sw = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp(), lastTitle = last;
        int framesSinceTitle = 0;
        string baseTitle = win.Text;
        while (true)
        {
            Application.DoEvents();
            if (win.UserClosed || ct.IsCancellationRequested) throw new OperationCanceledException();
            double t = sw.Elapsed.TotalSeconds;
            if (t >= warmup + measure) break;
            r.RenderScene((float)t, grid, layers, psIters);
            r.Present(vsync);
            long now = Stopwatch.GetTimestamp();
            double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
            last = now;
            if (t >= warmup) times.Add(ms);
            framesSinceTitle++;
            if (now - lastTitle > Stopwatch.Frequency / 2)
            {
                double fps = framesSinceTitle / ((now - lastTitle) / (double)Stopwatch.Frequency);
                win.Text = $"{baseTitle} · {fps:0} FPS{(t < warmup ? " · Aufwärmen" : "")}";
                lastTitle = now; framesSinceTitle = 0;
                report(t / (warmup + measure), fps);
            }
        }
        return times.ToArray();
    }
}

/// <summary>GPU: 3D render test (FPS / frame time, fixed scene at 1920×1080) + compute test (GFLOPS).</summary>
public sealed class GpuBenchmark : IBenchmark
{
    public string Id => "gpu";
    public string Title => "GPU";
    private const uint Grid = 44, Layers = 10, PsIters = 48;
    private const double Warmup = 3, Measure = 12, ComputeSeconds = 6;

    private readonly RenderSettings _set;
    public GpuBenchmark(RenderSettings settings) { _set = settings; }

    public Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct) =>
        RenderHost.Run("LAGZERO · GPU-Test", _set.Width, _set.Height, (win, r) =>
        {
            var res = new BenchResult { TestId = Id, Title = "GPU", StartedAt = DateTime.Now };
            var total = Stopwatch.StartNew();
            try
            {
                double[] ft = RenderHost.RenderLoop(win, r, Warmup, Measure, Grid, Layers, PsIters, vsync: false, ct,
                    (f, fps) => progress.Report(new BenchProgress(0.7 * f, $"3D-Test · {fps:0} FPS")));
                var st = FrameStats.Compute(ft);
                res.Add("gpu.3d.score", "3D-Punkte", st.AvgFps * 10, "Punkte (Ø FPS × 10)", true, "N0", "3D-Render-Test");
                res.Add("gpu.3d.fps", "FPS Ø", st.AvgFps, "FPS", true, "N1", "3D-Render-Test");
                res.Add("gpu.3d.low1", "1 % Low", st.Low1Fps, "FPS", true, "N1", "3D-Render-Test");
                res.Add("gpu.3d.ft.avg", "Frametime Ø", st.AvgMs, "ms", false, "0.000", "3D-Render-Test");
                res.Add("gpu.3d.ft.p99", "Frametime 99-%-Wert", st.P99Ms, "ms", false, "0.000", "3D-Render-Test");
                res.Add("gpu.3d.ft.max", "Frametime max", st.MaxMs, "ms", false, "0.000", "3D-Render-Test");
                res.Add("gpu.3d.time", "Dauer (Messung)", Measure, "s", false, "0", "3D-Render-Test");
                res.Series = ft; res.SeriesLabel = "Frametime 3D-Test (ms)";

                win.Text = $"LAGZERO · GPU-Test · {r.RenderWidth}×{r.RenderHeight} · Compute";
                r.RenderScene(0, 1, 1, 0); r.Present(false);
                var sw = Stopwatch.StartNew();
                double gflops = r.ComputeGflops(ComputeSeconds, ct, () => win.UserClosed,
                    f => progress.Report(new BenchProgress(0.7 + 0.3 * f, "Compute-Test (FMA) …")), out double gpuSec);
                res.Add("gpu.compute.score", "Compute-Punkte", gflops, "Punkte (GFLOPS)", true, "N0", "Compute-Test");
                res.Add("gpu.compute.gflops", "FP32-Durchsatz (FMA)", gflops / 1000.0, "TFLOPS", true, "0.00", "Compute-Test");
                res.Add("gpu.compute.time", "Dauer (GPU-Zeit)", gpuSec, "s", false, "0.00", "Compute-Test");
            }
            catch (OperationCanceledException) { res.Cancelled = true; }
            res.DurationSec = total.Elapsed.TotalSeconds;
            _set.WriteInfo(res, r);
            res.Notes.Add($"Grafikkarte: {r.AdapterName} · {SystemInfo.Gb(r.VramBytes)} VRAM · Direct3D 11 (Feature-Level {LevelName(r.Level)}).");
            res.Notes.Add($"3D: feste Szene {Grid * Grid * Layers:N0} Würfel (instanziert), Pixel-Shader mit {PsIters} ALU-Schleifen, " +
                          $"Renderauflösung {r.RenderWidth}×{r.RenderHeight}, ohne VSync{(r.Tearing ? " (Tearing erlaubt)" : "")}, " +
                          $"{Warmup:0} s Aufwärmen + {Measure:0} s Messung. 3D-Punkte = Ø FPS × 10.");
            res.Notes.Add("Compute: FMA-Ketten (FP32) im Compute-Shader, Zeit per GPU-Zeitstempel gemessen. Compute-Punkte = GFLOPS.");
            progress.Report(new BenchProgress(res.Cancelled ? 0 : 1, res.Cancelled ? "Abgebrochen" : "Fertig"));
            return res;
        });

    internal static string LevelName(FeatureLevel l) => l switch
    {
        FeatureLevel.Level_11_1 => "11_1", FeatureLevel.Level_11_0 => "11_0", _ => l.ToString(),
    };
}

/// <summary>Frame time test: own window, 10 s, FPS Ø, 1 % / 0,1 % Low, stutters, frame time history.</summary>
public sealed class FrametimeBenchmark : IBenchmark
{
    public string Id => "frametime";
    public string Title => "Frametime";
    private readonly RenderSettings _set;
    private bool _vsync => _set.VSync;
    private const uint Grid = 30, Layers = 6, PsIters = 16;
    private const double Warmup = 1.5, Measure = 10;

    public FrametimeBenchmark(RenderSettings settings) { _set = settings; }

    public Task<BenchResult> RunAsync(IProgress<BenchProgress> progress, CancellationToken ct) =>
        RenderHost.Run("LAGZERO · Frametime-Test", _set.Width, _set.Height, (win, r) =>
        {
            var res = new BenchResult { TestId = Id, Title = "Frametime", StartedAt = DateTime.Now };
            var total = Stopwatch.StartNew();
            try
            {
                double[] ft = RenderHost.RenderLoop(win, r, Warmup, Measure, Grid, Layers, PsIters, _vsync, ct,
                    (f, fps) => progress.Report(new BenchProgress(f, $"Frametime-Test · {fps:0} FPS")));
                var st = FrameStats.Compute(ft);
                res.Add("ft.fps", "FPS Ø", st.AvgFps, "FPS", true, "N1", "Bildrate");
                res.Add("ft.low1", "1 % Low", st.Low1Fps, "FPS", true, "N1", "Bildrate");
                res.Add("ft.low01", "0,1 % Low", st.Low01Fps, "FPS", true, "N1", "Bildrate");
                res.Add("ft.avg", "Frametime Ø", st.AvgMs, "ms", false, "0.000", "Frametime");
                res.Add("ft.median", "Frametime Median", st.MedianMs, "ms", false, "0.000", "Frametime");
                res.Add("ft.p99", "Frametime 99-%-Wert", st.P99Ms, "ms", false, "0.000", "Frametime");
                res.Add("ft.max", "Frametime max", st.MaxMs, "ms", false, "0.000", "Frametime");
                res.Add("ft.stutter", "Ruckler", st.Stutters, "Frames", false, "N0", "Ruckler");
                res.Add("ft.stutter.pct", "Ruckler-Anteil", st.Frames > 0 ? st.Stutters * 100.0 / st.Frames : 0, "%", false, "0.000", "Ruckler");
                res.Add("ft.frames", "Frames gesamt", st.Frames, "", true, "N0", "Ruckler");
                res.Series = ft; res.SeriesLabel = "Frametime (ms) über 10 s";
                res.Notes.Add($"Ruckler = Frame länger als 2 × Median und mindestens 4 ms über dem Median (Grenze hier {st.StutterThresholdMs:0.000} ms). 1 % / 0,1 % Low = 1000 / Ø der langsamsten 1 % / 0,1 % Frames.");
            }
            catch (OperationCanceledException) { res.Cancelled = true; }
            res.DurationSec = total.Elapsed.TotalSeconds;
            _set.WriteInfo(res, r);
            res.Notes.Add($"{r.AdapterName} · {(_vsync ? "VSync an" : "VSync aus" + (r.Tearing ? " (Tearing erlaubt)" : ""))} · " +
                          $"{Grid * Grid * Layers:N0} Würfel, {r.RenderWidth}×{r.RenderHeight} · {Warmup:0.#} s Aufwärmen + {Measure:0} s Messung. " +
                          "Gemessen wird der Abstand zwischen zwei Present-Aufrufen (max. 1 Frame Vorlauf).");
            progress.Report(new BenchProgress(res.Cancelled ? 0 : 1, res.Cancelled ? "Abgebrochen" : "Fertig"));
            return res;
        });
}

/// <summary>Resolution / VSync of a render test plus the primary monitor it ran on – stored with every result.</summary>
public sealed record RenderSettings(int Width, int Height, bool VSync, bool IsMonitorResolution)
{
    public static RenderSettings MonitorDefault(bool vsync = false)
    {
        var m = Monitors.Primary();
        return m != null ? new RenderSettings(m.Width, m.Height, vsync, true) : new RenderSettings(1920, 1080, vsync, false);
    }

    public string Resolution => $"{Width}×{Height}";

    internal void WriteInfo(BenchResult res, D3DRenderer r)
    {
        var m = Monitors.Primary();
        res.Info["resolution"] = $"{r.RenderWidth}×{r.RenderHeight}";
        res.Info["resolution_source"] = IsMonitorResolution ? "Monitor-Auflösung" : "fest gewählt";
        if (m != null)
        {
            res.Info["monitor"] = $"{m.Width}×{m.Height}";
            res.Info["refresh_hz"] = m.RefreshHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        res.Info["vsync"] = VSync ? "an" : "aus";
        res.Info["gpu"] = r.AdapterName;
        res.Info["vram_gb"] = (r.VramBytes / (1024.0 * 1024 * 1024)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        res.Info["feature_level"] = GpuBenchmark.LevelName(r.Level);
        if (SystemInfo.IsPortable()) res.Info["portable"] = "1";
    }
}