using Dwell.Core;
using Xunit;

namespace Dwell.Core.Tests;

public sealed class PowerTotalsTests
{
    [Fact]
    public void SupplyOutputIsUsedOnceWithoutItsRailsOrComponents()
    {
        var total = PowerTotals.Calculate(
        [
            new("PSU", PowerDeviceKind.Supply, [new("out", "Total Output", 95), new("rail", "+12V", 85)]),
            new("CPU", PowerDeviceKind.Cpu, [new("cpu", "CPU Package", 20)])
        ], 70);

        Assert.Equal(PowerSource.SupplyOutput, total.Source);
        Assert.Equal(95, total.Watts);
        Assert.Equal(["out"], total.SensorIds);
    }

    [Fact]
    public void ComponentsExcludeOverlappingSensorsAndUnknownReadings()
    {
        var total = PowerTotals.Calculate(
        [
            new("CPU", PowerDeviceKind.Cpu, [new("package", "CPU Package", 25), new("cores", "CPU Cores", 20)]),
            new("Integrated GPU", PowerDeviceKind.IntegratedGpu, [new("integrated", "GPU Power", 4)]),
            new("GPU", PowerDeviceKind.Gpu, [new("board", "GPU Package", 35), new("connector", "12VHPWR Connector", 30)]),
            new("SSD", PowerDeviceKind.Independent, [new("disk", "Power", 2)]),
            new("HDD", PowerDeviceKind.Independent, [new("missing", "Power", null)])
        ], null);

        Assert.Equal(PowerSource.Components, total.Source);
        Assert.Equal(62, total.Watts);
        Assert.Equal(["board", "disk", "package"], total.SensorIds);
    }

    [Fact]
    public void RaplDramAndCpuPackageAreCountedOnceAlongsideTheDiscreteGpu()
    {
        var devices = new PowerDevice[]
        {
            new("CPU", PowerDeviceKind.Cpu, [new("package", "CPU Package", 16), new("cores", "CPU Cores", 9)]),
            new("DRAM", PowerDeviceKind.RaplMemory, [new("dram", "RAPL DRAM", 2)]),
            new("DIMM", PowerDeviceKind.Memory, [new("dimm", "Power", 1)]),
            new("GPU", PowerDeviceKind.Gpu, [new("gpu", "GPU Package", 8)])
        };

        var total = PowerTotals.Calculate(devices, null);
        Assert.Equal(26, total.Watts);
        Assert.Equal(["dram", "gpu", "package"], total.SensorIds);
    }

    [Fact]
    public void BatteryDischargeTakesPrecedenceOverAnIncompleteSupply()
    {
        var devices = new PowerDevice[]
        {
            new("PSU 1", PowerDeviceKind.Supply, [new("out", "PSU Out", 50)]),
            new("PSU 2", PowerDeviceKind.Supply, [new("out2", "PSU Out", null)]),
            new("GPU", PowerDeviceKind.Gpu, [new("gpu", "GPU Package", 20)])
        };

        Assert.Equal(PowerSource.Battery, PowerTotals.Calculate(devices, 15).Source);
        Assert.Equal(15, PowerTotals.Calculate(devices, 15).Watts);
        Assert.Equal(PowerSource.Components, PowerTotals.Calculate(devices, null).Source);
        Assert.Null(PowerTotals.Calculate([], null).Watts);
    }
}
