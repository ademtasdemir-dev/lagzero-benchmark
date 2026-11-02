using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Lagzero.Core;

public sealed record MemoryModule(string Slot, long CapacityBytes, int SpeedMts, int ConfiguredMts, string Manufacturer, string PartNumber,
    int SmbiosType = 0, int FormFactor = 0, int DataWidth = 0, string BankLabel = "")
{
    /// <summary>Rated speed derived from the module part number (G.Skill / Corsair / Kingston FURY), else 0.</summary>
    public int PartNumberMts
    {
        get
        {
            var p = PartNumber.Trim();
            var m = Regex.Match(p, @"^F[345]-(\d{4})");                 // G.Skill F4-4000C17-...
            if (m.Success) return int.Parse(m.Groups[1].Value);
            m = Regex.Match(p, @"^CM[A-Z0-9]*?X[45]M\d[A-Z](\d{4})C");   // Corsair CMK16GX4M2B3200C16
            if (m.Success) return int.Parse(m.Groups[1].Value);
            m = Regex.Match(p, @"^KF(\d)(\d{2})C");                      // Kingston FURY KF436C17 / KF560C36
            if (m.Success) return int.Parse(m.Groups[2].Value) * 100;
            return 0;
        }
    }
}

public static class SystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);

    public static (ulong Total, ulong Available, uint LoadPct) MemoryStatus()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return (m.ullTotalPhys, m.ullAvailPhys, m.dwMemoryLoad);
    }

    public static ulong InstalledMemoryBytes() => GetPhysicallyInstalledSystemMemory(out var kb) ? kb * 1024 : 0;

    internal static IEnumerable<ManagementBaseObject> Wmi(string query, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(scope, query);
            foreach (var o in s.Get()) list.Add(o);
        }
        catch { /* WMI unavailable – return what we have */ }
        return list;
    }

    internal static string S(ManagementBaseObject o, string p)
    {
        try { return o[p]?.ToString()?.Trim() ?? ""; } catch { return ""; }
    }

    internal static long L(ManagementBaseObject o, string p)
    {
        try { return o[p] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; } catch { return 0; }
    }

    public static string Gb(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " GB";

    public static string CpuName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unbekannte CPU";
    }

    public static List<MemoryModule> MemoryModules() =>
        Wmi("SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor, DataWidth FROM Win32_PhysicalMemory")
            .Select(o => new MemoryModule(S(o, "DeviceLocator"), L(o, "Capacity"), (int)L(o, "Speed"), (int)L(o, "ConfiguredClockSpeed"),
                S(o, "Manufacturer"), S(o, "PartNumber"), (int)L(o, "SMBIOSMemoryType"), (int)L(o, "FormFactor"), (int)L(o, "DataWidth"), S(o, "BankLabel")))
            .ToList();

    public static string MemoryTypeName(int t) => t switch
    {
        24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => "unbekannt",
    };

    /// <summary>
    /// Populated 64-bit memory channels. Only reported as certain for socketed DIMM/SO-DIMM modules (DDR3/4/5) with readable
    /// channel labels; soldered / LPDDR memory (laptops) or unreadable labels → null ("nicht bewertbar").
    /// DDR5: one DIMM = 2 × 32-bit sub-channels = 64 bit, so it counts as one 64-bit channel. Channels never exceed the module count.
    /// </summary>
    public static (int? Channels, string Basis) MemoryChannels(List<MemoryModule> mods)
    {
        if (mods.Count == 0) return (null, "keine Moduldaten lesbar");
        var types = mods.Select(m => m.SmbiosType).Distinct().ToList();
        string typeName = string.Join("/", types.Select(MemoryTypeName));
        if (mods.Any(m => m.SmbiosType is 27 or 28 or 29 or 30 or 35))
            return (null, $"{typeName}: fest verlöteter Laptop-Speicher mit herstellerabhängiger Kanalbreite – Kanalzahl nicht sicher bestimmbar");
        if (mods.Any(m => m.FormFactor is not (8 or 12)))
            return (null, $"Bauform nicht DIMM/SO-DIMM (vermutlich verlötet) – Kanalzahl nicht sicher bestimmbar");
        if (mods.Any(m => m.SmbiosType is not (24 or 26 or 34 or 0)))
            return (null, $"Speichertyp {typeName} – Kanalbreite nicht sicher bestimmbar");
        if (mods.Any(m => m.DataWidth is not (0 or 64)))
            return (null, $"Datenbreite {string.Join("/", mods.Select(m => m.DataWidth).Distinct())} Bit je Modul – Kanalzahl nicht sicher bestimmbar");

        var keys = new HashSet<string>();
        foreach (var m in mods)
        {
            var r = Regex.Match(m.Slot + " " + m.BankLabel, @"(?:Controller\s*(\d+)|P(\d+)\b)?.*?CHANNEL\s*([A-Z])", RegexOptions.IgnoreCase);
            if (!r.Success) { keys.Clear(); break; }
            keys.Add($"{r.Groups[1].Value}{r.Groups[2].Value}/{r.Groups[3].Value.ToUpperInvariant()}");
        }
        string kind = typeName == "unbekannt" ? "" : typeName + ", ";
        if (keys.Count > 0)
        {
            int ch = Math.Min(keys.Count, mods.Count);
            return (ch, $"{kind}{mods.Count} Modul(e) in {ch} Kanal/Kanälen laut Steckplatz-Bezeichnungen ({string.Join(", ", mods.Select(m => m.Slot))})");
        }
        if (mods.Count == 1) return (1, $"{kind}nur ein Modul eingebaut → ein Kanal");
        return (null, "Kanalzuordnung aus den Steckplatz-Bezeichnungen nicht ablesbar – Kanalzahl unsicher");
    }
    public static int MemorySlots() =>
        (int)Wmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(o => L(o, "MemoryDevices"));

    public static string WindowsVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = k?.GetValue("ProductName") as string ?? "Windows";
        string display = k?.GetValue("DisplayVersion") as string ?? "";
        int build = int.TryParse(k?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        int ubr = k?.GetValue("UBR") is int u ? u : 0;
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11"); // ProductName is never updated for Win11
        return $"{product} {display} (Build {build}.{ubr}, {(Environment.Is64BitOperatingSystem ? "64-Bit" : "32-Bit")})".Replace("  ", " ");
    }

    /// <summary>Dedicated VRAM per adapter name from the display class registry (64-bit value, WMI AdapterRAM is capped at 4 GB).</summary>
    private static Dictionary<string, ulong> VramFromRegistry()
    {
        var d = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return d;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                var name = k?.GetValue("DriverDesc") as string;
                var mem = k?.GetValue("HardwareInformation.qwMemorySize");
                if (name == null || mem == null) continue;
                ulong v = mem switch { long l => (ulong)l, byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0), _ => 0 };
                if (v > 0) d[name] = v;
            }
        }
        catch { }
        return d;
    }

    public static List<InfoItem> Collect()
    {
        var items = new List<InfoItem>();
        void Add(string sec, string label, string value) { if (!string.IsNullOrWhiteSpace(value)) items.Add(new InfoItem(sec, label, value)); }

        // System
        Add("System", "Windows", WindowsVersion());
        foreach (var cs in Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            Add("System", "Hersteller / Modell", $"{S(cs, "Manufacturer")} {S(cs, "Model")}".Trim());

        // CPU
        Add("CPU", "Prozessor", CpuName());
        foreach (var p in Wmi("SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor"))
        {
            Add("CPU", "Kerne / Threads", $"{L(p, "NumberOfCores")} / {L(p, "NumberOfLogicalProcessors")}");
            Add("CPU", "Basistakt (WMI)", $"{L(p, "MaxClockSpeed")} MHz");
            Add("CPU", "Cache L2 / L3", $"{L(p, "L2CacheSize") / 1024.0:0.#} MB / {L(p, "L3CacheSize") / 1024.0:0.#} MB");
            var sock = S(p, "SocketDesignation"); if (!sock.Equals("CPUSocket", StringComparison.OrdinalIgnoreCase)) Add("CPU", "Sockel", sock);
        }

        // Mainboard / BIOS
        foreach (var b in Wmi("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
            Add("Mainboard", "Mainboard", $"{S(b, "Manufacturer")} {S(b, "Product")}".Trim());
        foreach (var b in Wmi("SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS"))
        {
            string date = S(b, "ReleaseDate");
            if (date.Length >= 8) date = $"{date[6..8]}.{date[4..6]}.{date[..4]}";
            Add("Mainboard", "BIOS", $"{S(b, "Manufacturer")} {S(b, "SMBIOSBIOSVersion")} ({date})");
        }

        // RAM
        var (total, avail, _) = MemoryStatus();
        var mods = MemoryModules();
        long sum = mods.Sum(m => m.CapacityBytes);
        Add("RAM", "Gesamt (installiert)", Gb(sum > 0 ? sum : (double)InstalledMemoryBytes()));
        Add("RAM", "Für Windows nutzbar", Gb(total));
        Add("RAM", "Aktuell frei", Gb(avail));
        if (mods.Count > 0)
        {
            int cfg = mods.Max(m => m.ConfiguredMts);
            Add("RAM", "Takt (konfiguriert)", cfg > 0 ? $"{cfg} MT/s" : "");
            int slots = MemorySlots();
            Add("RAM", "Module / Steckplätze", slots > 0 ? $"{mods.Count} von {slots} belegt" : $"{mods.Count} Module");
            foreach (var m in mods)
                Add("RAM", m.Slot, $"{Gb(m.CapacityBytes)} · {m.Manufacturer} {m.PartNumber} · {m.ConfiguredMts} MT/s");
        }

        // Monitor (physical pixels – EnumDisplaySettings is not affected by Windows scaling)
        if (Monitors.Primary() is { } mon)
            Add("Monitor", "Hauptmonitor", mon.ToString());

        // GPU
        var vram = VramFromRegistry();
        foreach (var g in Wmi("SELECT Name, DriverVersion, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            string name = S(g, "Name");
            ulong mem = vram.TryGetValue(name, out var v) ? v : (ulong)Math.Max(0, L(g, "AdapterRAM"));
            string res = L(g, "CurrentHorizontalResolution") > 0
                ? $" · {L(g, "CurrentHorizontalResolution")}×{L(g, "CurrentVerticalResolution")} @ {L(g, "CurrentRefreshRate")} Hz" : "";
            Add("GPU", name, $"{(mem > 0 ? Gb(mem) + " VRAM · " : "")}Treiber {S(g, "DriverVersion")}{res}");
        }

        // Drives
        foreach (var d in Wmi("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            Add("Laufwerke", S(d, "FriendlyName"),
                $"{Gb(L(d, "Size"))} · {MediaTypeName((int)L(d, "MediaType"))} · {BusTypeName((int)L(d, "BusType"))}");
        }

        // USB
        foreach (var u in UsbDevices())
            Add("USB-Geräte", u.Name, $"{u.VidPid} · {u.Class}{(u.Status is "OK" or "" ? "" : " · Status " + u.Status)}");
        return items;
    }

    public sealed record UsbDevice(string Name, string VidPid, string Class, string Status);

    /// <summary>Connected USB devices (top-level devices, no interfaces / hubs), with the name the device reports itself.</summary>
    public static List<UsbDevice> UsbDevices()
    {
        var list = new List<UsbDevice>();
        foreach (var o in Wmi("SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_%'"))
        {
            string id = S(o, "PNPDeviceID");
            if (id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;      // interface of a composite device
            string name = S(o, "Name"), cls = S(o, "PNPClass");
            if (cls.Equals("USB", StringComparison.OrdinalIgnoreCase) && name.Contains("Hub", StringComparison.OrdinalIgnoreCase)) continue;
            string reported = "";
            try
            {
                if (o is ManagementObject mo)
                {
                    var inParams = mo.GetMethodParameters("GetDeviceProperties");
                    inParams["devicePropertyKeys"] = new[] { "DEVPKEY_Device_BusReportedDeviceDesc" };
                    var outParams = mo.InvokeMethod("GetDeviceProperties", inParams, null);
                    if (outParams?["deviceProperties"] is ManagementBaseObject[] props && props.Length > 0)
                        reported = props[0]["Data"]?.ToString()?.Trim() ?? "";
                }
            }
            catch { }
            var m = Regex.Match(id, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
            string vp = m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : "";
            string display = !string.IsNullOrEmpty(reported) && !reported.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? $"{reported} ({name})" : name;
            list.Add(new UsbDevice(display, vp, cls, S(o, "Status")));
        }
        return list.OrderBy(u => u.Name).ToList();
    }

    /// <summary>Notebook / portable chassis (SMBIOS chassis type) or a built-in battery → GPU is a laptop variant.</summary>
    public static bool IsPortable()
    {
        int[] portable = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
        foreach (var o in Wmi("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            try { if (o["ChassisTypes"] is ushort[] types && types.Any(t => portable.Contains(t))) return true; } catch { }
        return Wmi("SELECT DeviceID FROM Win32_Battery").Any();
    }

    public static string MediaTypeName(int t) => t switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Medientyp unbekannt" };

    public static string BusTypeName(int t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "File-Backed Virtual", 16 => "Storage Spaces",
        17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "unbekannt",
    };
}
