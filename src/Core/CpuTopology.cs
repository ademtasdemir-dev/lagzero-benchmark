using System.Runtime.InteropServices;

namespace Lagzero.Core;

/// <summary>Hybrid CPU awareness (P-/E-cores) via Windows CPU sets.</summary>
public static class CpuTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr info, uint len, out uint returned, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[]? ids, uint count);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();

    public sealed record CpuSet(uint Id, byte CoreIndex, byte EfficiencyClass);

    private static List<CpuSet>? _sets;

    public static List<CpuSet> Sets
    {
        get
        {
            if (_sets != null) return _sets;
            var list = new List<CpuSet>();
            try
            {
                GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint len, IntPtr.Zero, 0);
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0))
                    {
                        int off = 0;
                        while (off < len)
                        {
                            int size = Marshal.ReadInt32(buf, off);
                            int type = Marshal.ReadInt32(buf, off + 4);
                            if (type == 0) // CpuSetInformation
                                list.Add(new CpuSet((uint)Marshal.ReadInt32(buf, off + 8), Marshal.ReadByte(buf, off + 15), Marshal.ReadByte(buf, off + 18)));
                            if (size <= 0) break;
                            off += size;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return _sets = list;
        }
    }

    public static bool IsHybrid => Sets.Select(s => s.EfficiencyClass).Distinct().Count() > 1;

    public static (int PCores, int ECores) CoreCounts()
    {
        if (!IsHybrid) return (Sets.Select(s => s.CoreIndex).Distinct().Count(), 0);
        byte max = Sets.Max(s => s.EfficiencyClass);
        return (Sets.Where(s => s.EfficiencyClass == max).Select(s => s.CoreIndex).Distinct().Count(),
                Sets.Where(s => s.EfficiencyClass != max).Select(s => s.CoreIndex).Distinct().Count());
    }

    /// <summary>Restricts the calling thread to the performance cores (highest efficiency class). No-op on non-hybrid CPUs.</summary>
    public static bool PinCurrentThreadToPerformanceCores()
    {
        if (!IsHybrid) return false;
        byte max = Sets.Max(s => s.EfficiencyClass);
        var ids = Sets.Where(s => s.EfficiencyClass == max).Select(s => s.Id).ToArray();
        return SetThreadSelectedCpuSets(GetCurrentThread(), ids, (uint)ids.Length);
    }
}
