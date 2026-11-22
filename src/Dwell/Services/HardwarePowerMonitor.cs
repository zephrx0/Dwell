using System.Runtime.InteropServices;
using Dwell.Core;
using LibreHardwareMonitor.Hardware;

namespace Dwell.Services;

internal sealed record HardwarePowerSample(
    PowerTotal Total, IReadOnlyList<PowerDevice> Devices, bool GpuPollingPaused = false);

internal sealed class HardwarePowerMonitor(bool emiOnly = false) : IDisposable
{
    private readonly object _gate = new();
    private readonly WindowsEnergyMeters _energyMeters = new();
    private Computer? _computer;
    private readonly Dictionary<string, PowerDeviceKind> _radeons = new();
    private long _retryAt;
    private bool _disposed;

    public HardwarePowerSample Read()
    {
        lock (_gate)
        {
            if (_disposed) return new(PowerTotal.Unavailable, []);
            var devices = new List<PowerDevice>();
            var cpuIndices = new Dictionary<int, int>();
            var pollGpu = !emiOnly && GpuPowerState.CanPoll();
            if (!emiOnly && OpenComputer(pollGpu) is { } computer)
            {
                foreach (var hardware in computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.Cpu && int.TryParse(hardware.Identifier.ToString().Split('/')[^1], out var cpuIndex))
                        cpuIndices.TryAdd(cpuIndex, devices.Count);
                    ReadDevice(hardware, devices, includeWithoutSensors: true);
                }
            }

            AddWindowsReadings(devices, cpuIndices, _energyMeters.Read());
            return new(PowerTotals.Calculate(devices), devices, !pollGpu);
        }
    }

    private Computer? OpenComputer(bool pollGpu)
    {
        if (_computer is not null)
        {
            if (_computer.IsGpuEnabled != pollGpu) _computer.IsGpuEnabled = pollGpu;
            return _computer;
        }
        if (Environment.TickCount64 < _retryAt) return null;
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = pollGpu };
        try
        {
            computer.Open();
            return _computer = computer;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            _retryAt = Environment.TickCount64 + 30_000;
            CloseComputer(computer);
            return null;
        }
    }

    private void ReadDevice(IHardware hardware, List<PowerDevice> devices, bool includeWithoutSensors)
    {
        var id = hardware.Identifier.ToString();
        var kind = hardware.HardwareType switch
        {
            HardwareType.Cpu when id.StartsWith("/amdcpu/", StringComparison.Ordinal) => PowerDeviceKind.AmdCpu,
            HardwareType.Cpu => PowerDeviceKind.Cpu,
            HardwareType.GpuIntel when id.StartsWith("/gpu-intel-integrated/", StringComparison.Ordinal) => PowerDeviceKind.IntegratedGpu,
            HardwareType.GpuAmd => RadeonKind(id),
            HardwareType.GpuNvidia or HardwareType.GpuIntel => PowerDeviceKind.Gpu,
            _ => PowerDeviceKind.Other
        };

        if (kind == PowerDeviceKind.Other) return;
        PowerSensor[] sensors = [];
        double? loadPercent = null;
        try
        {
            hardware.Update();
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                var load = hardware.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Load && x.Name == "CPU Total")?.Value;
                if (load is >= 0 and <= 100) loadPercent = load;
            }
            sensors = hardware.Sensors.Where(x => x.SensorType == SensorType.Power && x.Name is not ("CPU Memory" or "CPU Platform"))
                .Select(x => ReadHardwareSensor(x, hardware.HardwareType == HardwareType.Cpu))
                .ToArray();
            // LibreHardwareMonitor can expose an all-zero core set when it cannot read the CPU counters.
            if (hardware.HardwareType == HardwareType.Cpu &&
                !sensors.Any(x => x.Kind == PowerSensorKind.HardwareCpuCore && x.Watts > 0))
                sensors = sensors.Select(x => x.Watts == 0 ? x with { Watts = null } : x).ToArray();
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }

        if (sensors.Length > 0 || (includeWithoutSensors && id != "/vram"))
            devices.Add(new(hardware.Name, kind, sensors, loadPercent));
        foreach (var child in hardware.SubHardware)
            ReadDevice(child, devices, includeWithoutSensors: false);
    }

    private PowerDeviceKind RadeonKind(string id)
    {
        if (_radeons.TryGetValue(id, out var kind)) return kind;
        kind = PowerDeviceKind.AmdGpu;
        if (int.TryParse(id.Split('/')[^1], out var index))
        {
            try
            {
                if (ADL2_Main_Control_Create(Allocate, 1, out var context) == 0)
                {
                    try
                    {
                        if (ADL2_Adapter_ASICFamilyType_Get(context, index, out var flags, out var valid) == 0)
                        {
                            if ((flags & valid & 2) != 0) kind = PowerDeviceKind.IntegratedGpu;
                            else if ((flags & valid & 1) != 0) kind = PowerDeviceKind.Gpu;
                        }
                    }
                    finally { ADL2_Main_Control_Destroy(context); }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return _radeons[id] = kind;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AdlAllocate(int size);
    private static readonly AdlAllocate Allocate = Marshal.AllocHGlobal;
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(AdlAllocate allocate, int connected, out IntPtr context);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_ASICFamilyType_Get(IntPtr context, int index, out int types, out int valid);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);

    private static PowerSensor ReadHardwareSensor(ISensor sensor, bool isCpu)
    {
        const string corePrefix = "Core #";
        const string coreSuffix = " (SMU)";
        var name = sensor.Name;
        var kind = isCpu && name.StartsWith(corePrefix, StringComparison.Ordinal) &&
            name.EndsWith(coreSuffix, StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(corePrefix.Length, name.Length - corePrefix.Length - coreSuffix.Length), out _)
            ? PowerSensorKind.HardwareCpuCore : PowerSensorKind.Other;
        var watts = sensor.Value is >= 0 and var value && float.IsFinite(value) &&
            (!isCpu || value > 0 || kind == PowerSensorKind.HardwareCpuCore) ? value : (double?)null;
        return new(sensor.Identifier.ToString(), name, watts, kind);
    }

    private static void AddWindowsReadings(List<PowerDevice> devices, Dictionary<int, int> cpuIndices, List<EnergyChannel> readings)
    {
        var cpuReadings = new Dictionary<int, List<PowerSensor>>();
        foreach (var reading in readings.OrderByDescending(x => x.Watts.HasValue))
        {
            var parts = reading.Name.ToUpperInvariant().Split('_');
            var coreChannel = parts is ["RAPL", _, var core, "CORE"] && core.StartsWith("CORE", StringComparison.Ordinal) &&
                int.TryParse(core.AsSpan(4), out _);
            var package = 0;
            string? name;
            if ((parts.Length == 3 || coreChannel) && parts[0] == "RAPL" &&
                parts[1].StartsWith("PACKAGE", StringComparison.Ordinal) && int.TryParse(parts[1].AsSpan(7), out package))
            {
                name = parts[2] switch
                {
                    "PKG" or "PACKAGE" => "CPU Package",
                    "PP0" => "CPU Cores",
                    "PP1" => "CPU Graphics",
                    _ when coreChannel => "Core" + parts[2][4..] + " (Windows)",
                    _ => null
                };
            }
            else if (cpuIndices.Count == 1)
            {
                package = cpuIndices.Keys.Single();
                name = reading.Name.ToUpperInvariant() switch
                {
                    "CPU PACKAGE" or "PROCESSOR PACKAGE" or "CPU PACKAGE POWER" => "CPU Package",
                    "CPU CORES" or "CPU CORE POWER" => "CPU Cores",
                    _ => null
                };
            }
            else continue;
            if (name is null) continue;
            if (!cpuReadings.TryGetValue(package, out var sensors)) cpuReadings[package] = sensors = [];
            var kind = coreChannel ? PowerSensorKind.WindowsCpuCore : PowerSensorKind.Other;
            if (!sensors.Any(x => x.Name == name))
                sensors.Add(new(reading.Id, name, reading.Watts, kind, reading.AverageSeconds, coreChannel ? reading.Name : null));
        }

        foreach (var (package, readingsForCpu) in cpuReadings)
        {
            if (!cpuIndices.TryGetValue(package, out var index))
            {
                devices.Add(new($"CPU {package + 1}", PowerDeviceKind.Cpu, readingsForCpu.ToArray()));
                continue;
            }

            var device = devices[index];
            var sensors = device.Sensors.ToList();
            foreach (var reading in readingsForCpu)
            {
                var existing = sensors.FindIndex(x => x.Name == reading.Name || x.Name == "Package" && reading.Name == "CPU Package");
                if (existing < 0) sensors.Add(reading);
                else if (sensors[existing].Watts is null) sensors[existing] = reading;
            }
            devices[index] = device with { Sensors = sensors.ToArray() };
        }
    }

    private static void CloseComputer(Computer computer)
    {
        try { computer.Close(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _energyMeters.Dispose();
            if (_computer is not null) CloseComputer(_computer);
            _computer = null;
        }
    }
}
