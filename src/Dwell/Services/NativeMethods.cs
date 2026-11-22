using System.Runtime.InteropServices;
using System.Text;

namespace Dwell.Services;

internal static class NativeMethods
{
    internal delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] internal struct LastInputInfo { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] private record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Bounds, WorkArea; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 16)] internal struct GamepadState { public uint Packet; }
    [DllImport("xinput1_4.dll")] internal static extern uint XInputGetState(uint index, out GamepadState state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBox(nint hwnd, string text, string caption, uint type);
    [StructLayout(LayoutKind.Sequential)] private struct HighContrastInfo { public uint Size; public uint Flags; public nint Scheme; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrastInfo info, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect rect, uint length);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllowSetForegroundWindow(uint processId);
    internal delegate bool EnumWindowProc(nint hwnd, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint hwnd, EnumWindowProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
    [DllImport("kernel32.dll")] internal static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSUnRegisterSessionNotification(nint hwnd);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSQuerySessionInformation(nint server, uint session, int infoClass, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(nint memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateEvent(nint attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetEvent(nint handle);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    internal static bool IsSessionLocked()
    {
        // WTSINFOEX: level=1, session flags at offset 16 (Win10+).
        if (!WTSQuerySessionInformation(0, uint.MaxValue, 25, out var buffer, out var bytes)) return true;
        try { return bytes < 20 || Marshal.ReadInt32(buffer) != 1 || Marshal.ReadInt32(buffer, 16) == 0; }
        finally { WTSFreeMemory(buffer); }
    }

    internal static bool IsFullScreen(nint hwnd)
    {
        if (hwnd == 0 || DwmGetWindowAttribute(hwnd, 9, out var bounds, (uint)Marshal.SizeOf<Rect>()) != 0)
            return false;
        var monitor = MonitorFromWindow(hwnd, 0);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info) && bounds == info.Bounds;
    }

    internal static bool HighContrastEnabled()
    {
        var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfo(0x0042, info.Size, ref info, 0) && (info.Flags & 1) != 0;
    }
}
