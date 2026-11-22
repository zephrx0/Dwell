using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dwell.Services;

internal static class GpuPowerState
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint PresentDisplayDevices = 0x300;
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    public static bool CanPoll()
    {
        // On battery, avoid opening vendor GPU interfaces. AC-powered monitoring still checks the PnP power state.
        if (GetSystemPowerStatus(out var status) && status.BatteryFlag != 128 && status.AcLineStatus != 1) return false;
        if (CM_Get_Device_ID_List_SizeW(out var length, DisplayClass, PresentDisplayDevices) != 0 || length is < 2 or > 65_536)
            return false;
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(DisplayClass, buffer, length, PresentDisplayDevices) != 0) return false;
        var devices = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)).ToArray();
        return devices.Length > 0 && devices.All(IsAwake);
    }

    private static bool IsAwake(string id)
    {
        if (CM_Locate_DevNodeW(out var device, id, 0) != 0) return false;
        var key = new DevicePropertyKey { Category = DeviceProperties, Id = 32 };
        var data = new byte[64];
        var length = (uint)data.Length;
        return CM_Get_DevNode_PropertyW(device, ref key, out _, data, ref length, 0) == 0 && length >= 8
            && BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) == 1; // CM_POWER_DATA.PD_MostRecentPowerState == PowerDeviceD0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatus;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DevicePropertyKey { public Guid Category; public uint Id; }

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint device, string instance, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint device, ref DevicePropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint length, uint flags);
}
