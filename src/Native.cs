using System.Runtime.InteropServices;

namespace Lagzero;

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LX, LY, RX, RY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputCapabilitiesEx
{
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XInputGamepad Gamepad;
    public ushort VibLeft, VibRight;
    public ushort VendorId, ProductId, VersionNumber, Unknown1;
    public uint Unknown2;
}

[Flags]
public enum XButtons : ushort
{
    DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

public sealed record ControllerInfo(uint Index, string Name, ushort Vid, ushort Pid, byte SubType)
{
    public string VidPid => Vid == 0 && Pid == 0 ? "-" : $"VID_{Vid:X4} PID_{Pid:X4}";
}

public static class XInput
{
    public const uint Success = 0;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetStateRaw(uint userIndex, out XInputState state);

    /// <summary>False if XInput is missing on this system (then every slot reads as "not connected").</summary>
    public static bool Available { get; private set; } = true;

    public static uint GetState(uint userIndex, out XInputState state)
    {
        state = default;
        if (!Available) return 1167; // ERROR_DEVICE_NOT_CONNECTED
        try { return GetStateRaw(userIndex, out state); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Available = false; return 1167; }
    }

    // Undocumented, exported by ordinal only (xinput1_4, Windows 8+). Returns VID/PID.
    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint GetCapabilitiesEx(uint one, uint userIndex, uint flags, out XInputCapabilitiesEx caps);

    public static bool IsConnected(uint index) => GetState(index, out _) == Success;

    public static ControllerInfo Describe(uint index)
    {
        ushort vid = 0, pid = 0; byte sub = 0;
        try
        {
            if (GetCapabilitiesEx(1, index, 0, out var c) == Success)
            {
                vid = c.VendorId; pid = c.ProductId; sub = c.SubType;
            }
        }
        catch { /* ordinal not available */ }
        return new ControllerInfo(index, KnownName(vid, pid, sub), vid, pid, sub);
    }

    private static string KnownName(ushort vid, ushort pid, byte subType)
    {
        if (vid == 0x045E)
        {
            return pid switch
            {
                0x028E => "Xbox 360 Controller for Windows",
                0x028F => "Xbox 360 Wireless Controller",
                0x02D1 or 0x02DD or 0x02EA or 0x0B12 => "Xbox One / Series Controller",
                0x02E3 or 0x0B00 => "Xbox Elite Controller",
                _ => "Microsoft XInput Controller",
            };
        }
        return subType switch
        {
            1 => "XInput Gamepad",
            2 => "XInput Wheel",
            3 => "XInput Arcade Stick",
            4 => "XInput Flight Stick",
            _ => "XInput Controller",
        };
    }
}

public static class WinTimer
{
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);

    /// <summary>Current system timer resolution in ms (or NaN).</summary>
    public static double CurrentResolutionMs()
    {
        try { return NtQueryTimerResolution(out _, out _, out var cur) == 0 ? cur / 10000.0 : double.NaN; }
        catch { return double.NaN; }
    }

    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
}
