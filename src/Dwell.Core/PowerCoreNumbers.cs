using System.Globalization;

namespace Dwell.Core;

public static class PowerCoreNumbers
{
    private static readonly StringComparer SensorOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);

    public static Dictionary<string, int> ForDevice(PowerDevice device) => device.Sensors
        .Where(sensor => sensor.Kind is PowerSensorKind.WindowsCpuCore or PowerSensorKind.HardwareCpuCore)
        .DistinctBy(sensor => sensor.Id)
        .GroupBy(sensor => sensor.Kind)
        .SelectMany(group => group.OrderBy(sensor => sensor.Name, SensorOrder)
            .Select((sensor, index) => (sensor.Id, Number: index + 1)))
        .ToDictionary(core => core.Id, core => core.Number, StringComparer.Ordinal);
}
