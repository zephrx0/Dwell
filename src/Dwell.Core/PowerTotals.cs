namespace Dwell.Core;

public enum PowerDeviceKind { Other, Cpu, AmdCpu, Gpu, AmdGpu, IntegratedGpu, Independent, Memory, RaplMemory, Supply }
public enum PowerSource { None, Components, SupplyOutput, Battery }
public enum PowerSensorKind { Other, HardwareCpuCore, WindowsCpuCore, EstimatedDiskPower }

public sealed record PowerSensor(string Id, string Name, double? Watts, PowerSensorKind Kind = PowerSensorKind.Other);
public sealed record PowerDevice(string Name, PowerDeviceKind Kind, PowerSensor[] Sensors);
public sealed record PowerTotal(PowerSource Source, double? Watts, string[] SensorIds, bool IsCpuPartial = false, bool IsEstimated = false)
{
    public static PowerTotal Unavailable { get; } = new(PowerSource.None, null, []);
}

public static class PowerTotals
{
    public static PowerTotal Calculate(IReadOnlyList<PowerDevice> devices, double? batteryWatts, bool onBattery = true)
    {
        var supplies = devices.Where(x => x.Kind == PowerDeviceKind.Supply).ToArray();
        var output = supplies.Select(x => Find(x, "PSU Out", "Total Output", "Total watts")).ToArray();
        if (output.Length > 0 && output.All(x => x is not null))
            return Sum(PowerSource.SupplyOutput, output.OfType<PowerSensor>());

        if (onBattery && batteryWatts is > 0 && double.IsFinite(batteryWatts.Value))
            return new(PowerSource.Battery, batteryWatts, []);

        var cpu = devices.Where(x => x.Kind is PowerDeviceKind.Cpu or PowerDeviceKind.AmdCpu)
            .Select(x => (Device: x, Package: FindCpuPackage(x))).ToArray();
        var includesCpuPackage = cpu.Any(x => x.Package is not null);
        var includesAmdPackage = cpu.Any(x => x.Device.Kind == PowerDeviceKind.AmdCpu && x.Package is not null);
        var includesRaplMemory = devices.Any(x => x.Kind == PowerDeviceKind.RaplMemory && x.Sensors.Any(Available));
        var components = devices.Select(x => x.Kind switch
        {
            PowerDeviceKind.Gpu => FindGpu(x),
            PowerDeviceKind.AmdGpu when !includesAmdPackage || System.Text.RegularExpressions.Regex.IsMatch(x.Name, @"\bRadeon(?:\(TM\))?\s+(?:RX\s+\d|PRO\s+WX?\s*\d|VII\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) => FindGpu(x),
            PowerDeviceKind.IntegratedGpu when !includesCpuPackage => FindGpu(x),
            PowerDeviceKind.Independent or PowerDeviceKind.RaplMemory => FindIndependent(x),
            PowerDeviceKind.Memory when !includesRaplMemory => FindIndependent(x),
            _ => null
        });
        var cpuReadings = cpu.SelectMany(x => x.Package is { } package ? [package] : FindCpuCores(x.Device));
        return Sum(PowerSource.Components, cpuReadings.Concat(components.OfType<PowerSensor>()),
            isCpuPartial: cpu.Any(x => x.Package is null));
    }

    private static PowerSensor? FindCpuPackage(PowerDevice device) =>
        Find(device, "CPU Package", "Package") ?? (device.Kind == PowerDeviceKind.AmdCpu ? Find(device, "CPU PPT") : null);

    private static IEnumerable<PowerSensor> FindCpuCores(PowerDevice device)
    {
        var aggregate = Find(device, "CPU Cores") ??
            (device.Kind == PowerDeviceKind.AmdCpu ? Find(device, "Core Power") : null);
        if (aggregate is not null) return [aggregate];

        // The two providers can expose the same physical cores under different numbers.
        var cores = device.Sensors.Where(x => x.Kind is PowerSensorKind.HardwareCpuCore or PowerSensorKind.WindowsCpuCore);
        return cores.Where(Available).DistinctBy(x => x.Id).GroupBy(x => x.Kind)
            .OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key == PowerSensorKind.WindowsCpuCore)
            .FirstOrDefault() ?? Enumerable.Empty<PowerSensor>();
    }

    private static PowerSensor? FindGpu(PowerDevice device) =>
        Find(device, "GPU Total", "GPU Board Power", "GPU Package", "GPU PPT", "GPU Power", "GPU Core");

    private static PowerSensor? FindIndependent(PowerDevice device) =>
        Find(device, "Total Power", "Power Consumption", "Power") ??
        (device.Sensors is [var only] && Available(only) ? only : null) ??
        device.Sensors.FirstOrDefault(x => x.Kind == PowerSensorKind.EstimatedDiskPower && Available(x));

    private static PowerSensor? Find(PowerDevice device, params string[] sensorNames)
    {
        foreach (var name in sensorNames)
            if (device.Sensors.FirstOrDefault(x => x.Name == name && Available(x)) is { } sensor) return sensor;
        return null;
    }

    private static bool Available(PowerSensor sensor) => sensor.Watts is >= 0 && double.IsFinite(sensor.Watts.Value);

    private static PowerTotal Sum(PowerSource source, IEnumerable<PowerSensor> readings, bool isCpuPartial = false)
    {
        var sensors = readings.DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return sensors.Length == 0 ? PowerTotal.Unavailable :
            new(source, sensors.Sum(x => x.Watts!.Value), sensors.Select(x => x.Id).ToArray(), isCpuPartial,
                sensors.Any(x => x.Kind == PowerSensorKind.EstimatedDiskPower));
    }
}
