namespace Dwell.Core;

public enum PowerDeviceKind { Other, Cpu, AmdCpu, Gpu, AmdGpu, IntegratedGpu, Independent, Supply }
public enum PowerSource { None, Components, SupplyOutput, Battery }

public sealed record PowerSensor(string Id, string Name, double? Watts);
public sealed record PowerDevice(string Name, PowerDeviceKind Kind, PowerSensor[] Sensors);
public sealed record PowerTotal(PowerSource Source, double? Watts, string[] SensorIds)
{
    public static PowerTotal Unavailable { get; } = new(PowerSource.None, null, []);
}

public static class PowerTotals
{
    public static PowerTotal Calculate(IReadOnlyList<PowerDevice> devices, double? batteryWatts)
    {
        var supplies = devices.Where(x => x.Kind == PowerDeviceKind.Supply).ToArray();
        var output = supplies.Select(x => Find(x, "PSU Out", "Total Output", "Total watts")).ToArray();
        if (output.Length > 0 && output.All(x => x is not null))
            return Sum(PowerSource.SupplyOutput, output.OfType<PowerSensor>());

        if (batteryWatts is > 0 && double.IsFinite(batteryWatts.Value))
            return new(PowerSource.Battery, batteryWatts, []);

        var cpu = devices.Where(x => x.Kind is PowerDeviceKind.Cpu or PowerDeviceKind.AmdCpu)
            .Select(x => (x.Kind, Sensor: Find(x, "CPU Package", "Package", "CPU Cores"))).ToArray();
        var includesCpuPackage = cpu.Any(x => x.Sensor is { Name: "CPU Package" or "Package" });
        var includesAmdPackage = cpu.Any(x => x.Kind == PowerDeviceKind.AmdCpu && x.Sensor is { Name: "CPU Package" or "Package" });
        var components = devices.Select(x => x.Kind switch
        {
            PowerDeviceKind.Gpu => FindGpu(x),
            PowerDeviceKind.AmdGpu when !includesAmdPackage => FindGpu(x),
            PowerDeviceKind.IntegratedGpu when !includesCpuPackage => FindGpu(x),
            PowerDeviceKind.Independent => Find(x, "Total Power", "Power Consumption", "Power") ??
                (x.Sensors is [var only] && Available(only) ? only : null),
            _ => null
        });
        return Sum(PowerSource.Components, cpu.Select(x => x.Sensor).Concat(components).OfType<PowerSensor>());
    }

    private static PowerSensor? FindGpu(PowerDevice device) =>
        Find(device, "GPU Total", "GPU Board Power", "GPU Package", "GPU PPT", "GPU Power", "GPU Core");

    private static PowerSensor? Find(PowerDevice device, params string[] sensorNames)
    {
        foreach (var name in sensorNames)
            if (device.Sensors.FirstOrDefault(x => x.Name == name && Available(x)) is { } sensor) return sensor;
        return null;
    }

    private static bool Available(PowerSensor sensor) => sensor.Watts is >= 0 && double.IsFinite(sensor.Watts.Value);

    private static PowerTotal Sum(PowerSource source, IEnumerable<PowerSensor> readings)
    {
        var sensors = readings.DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return sensors.Length == 0 ? PowerTotal.Unavailable :
            new(source, sensors.Sum(x => x.Watts!.Value), sensors.Select(x => x.Id).ToArray());
    }
}
