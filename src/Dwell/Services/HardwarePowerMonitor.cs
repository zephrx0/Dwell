using System.Management;
using System.Runtime.InteropServices;
using Dwell.Core;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;
using Windows.Devices.Power;
using Windows.System.Power;

namespace Dwell.Services;

internal sealed record HardwarePowerSample(PowerTotal Total, IReadOnlyList<PowerDevice> Devices, double? BatteryWatts);

internal sealed class HardwarePowerMonitor : IDisposable
{
    private readonly object _gate = new();
    private Computer? _computer;
    private (int Index, string Name)[]? _disks;
    private long _retryAt;
    private bool _disposed;

    public HardwarePowerSample Read()
    {
        lock (_gate)
        {
            if (_disposed) return new(PowerTotal.Unavailable, [], null);
            var devices = new List<PowerDevice>();
            var storageIndices = new HashSet<int>();
            if (OpenComputer() is { } computer)
            {
                foreach (var hardware in computer.Hardware)
                {
                    ReadDevice(hardware, devices, includeWithoutSensors: true);
                    if (hardware is StorageDevice storage) storageIndices.Add(storage.Storage.DriveNumber);
                }
            }

            _disks ??= ReadDisks();
            devices.AddRange(_disks.Where(x => !storageIndices.Contains(x.Index))
                .Select(x => new PowerDevice(x.Name, PowerDeviceKind.Independent, [])));
            var battery = ReadBattery();
            return new(PowerTotals.Calculate(devices, battery), devices, battery);
        }
    }

    private Computer? OpenComputer()
    {
        if (_computer is not null) return _computer;
        if (Environment.TickCount64 < _retryAt) return null;
        var computer = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
            IsMotherboardEnabled = true, IsStorageEnabled = true, IsPsuEnabled = true,
            IsBatteryEnabled = true, IsControllerEnabled = true, IsPowerMonitorEnabled = true
        };
        try
        {
            computer.Open();
            return _computer = computer;
        }
        catch (Exception ex) when (SensorError(ex))
        {
            _retryAt = Environment.TickCount64 + 30_000;
            CloseComputer(computer);
            return null;
        }
    }

    private static void ReadDevice(IHardware hardware, List<PowerDevice> devices, bool includeWithoutSensors)
    {
        var id = hardware.Identifier.ToString();
        var kind = hardware.HardwareType switch
        {
            HardwareType.Cpu when id.StartsWith("/amdcpu/", StringComparison.Ordinal) => PowerDeviceKind.AmdCpu,
            HardwareType.Cpu => PowerDeviceKind.Cpu,
            HardwareType.GpuIntel when id.StartsWith("/gpu-intel-integrated/", StringComparison.Ordinal) => PowerDeviceKind.IntegratedGpu,
            HardwareType.GpuAmd => PowerDeviceKind.AmdGpu,
            HardwareType.GpuNvidia or HardwareType.GpuIntel => PowerDeviceKind.Gpu,
            HardwareType.Psu => PowerDeviceKind.Supply,
            HardwareType.Storage or HardwareType.Memory or HardwareType.Cooler => PowerDeviceKind.Independent,
            _ => PowerDeviceKind.Other
        };

        PowerSensor[] sensors = [];
        try
        {
            hardware.Update();
            sensors = hardware.Sensors.Where(x => x.SensorType == SensorType.Power)
                .Select(x => new PowerSensor(x.Identifier.ToString(), x.Name, SensorWatts(x.Value, hardware.HardwareType == HardwareType.Cpu)))
                .ToArray();
        }
        catch (Exception ex) when (SensorError(ex)) { }

        if (sensors.Length > 0 || (includeWithoutSensors && id != "/vram"))
            devices.Add(new(hardware.Name, kind, sensors));
        foreach (var child in hardware.SubHardware)
            ReadDevice(child, devices, includeWithoutSensors: false);
    }

    private static double? SensorWatts(float? value, bool isCpu) =>
        value is >= 0 && float.IsFinite(value.Value) && (!isCpu || value > 0) ? value.Value : null;

    private static (int Index, string Name)[] ReadDisks()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            using var results = query.Get();
            var disks = new List<(int, string)>();
            foreach (ManagementObject disk in results)
            {
                using (disk)
                    if (disk["Index"] is uint index && disk["Model"] is string name)
                        disks.Add(((int)index, name.Trim()));
            }
            return disks.OrderBy(x => x.Item1).ToArray();
        }
        catch (Exception ex) when (SensorError(ex) || ex is ManagementException) { return []; }
    }

    private static double? ReadBattery()
    {
        try
        {
            var report = Battery.AggregateBattery.GetReport();
            return report.Status == BatteryStatus.Discharging && report.ChargeRateInMilliwatts is < 0 and var rate
                ? rate / -1000d : null;
        }
        catch (Exception ex) when (SensorError(ex)) { return null; }
    }

    private static bool SensorError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
        or ExternalException or InvalidOperationException or NotSupportedException or TypeInitializationException or DllNotFoundException;

    private static void CloseComputer(Computer computer)
    {
        try { computer.Close(); }
        catch (Exception ex) when (SensorError(ex)) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_computer is not null) CloseComputer(_computer);
            _computer = null;
        }
    }
}
