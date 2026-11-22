using Dwell.Core;
using Xunit;

namespace Dwell.Core.Tests;

public sealed class PowerTotalsTests
{
    [Theory]
    [InlineData("AMD Radeon Pro WX 7100")]
    [InlineData("AMD Radeon VII")]
    public void OlderDiscreteRadeonCountsBesideRyzen(string gpuName)
    {
        var total = PowerTotals.Calculate([
            new("Ryzen", PowerDeviceKind.AmdCpu, [new("cpu", "CPU Package", 40)]),
            new(gpuName, PowerDeviceKind.AmdGpu, [new("gpu", "GPU Package", 75)])
        ]);

        Assert.Equal(PowerSource.Components, total.Source);
        Assert.Equal(115, total.Watts);
    }

    [Fact]
    public void ComponentsExcludeOverlappingSensorsAndUnknownReadings()
    {
        var total = PowerTotals.Calculate(
        [
            new("CPU", PowerDeviceKind.Cpu, [new("package", "CPU Package", 25), new("cores", "CPU Cores", 20)]),
            new("Integrated GPU", PowerDeviceKind.IntegratedGpu, [new("integrated", "GPU Power", 4)]),
            new("GPU", PowerDeviceKind.Gpu, [new("board", "GPU Package", 35), new("connector", "12VHPWR Connector", 30)]),
            new("SSD", PowerDeviceKind.Other, [new("disk", "Power", 2)]),
            new("HDD", PowerDeviceKind.Other, [new("missing", "Power", null)])
        ]);

        Assert.Equal(PowerSource.Components, total.Source);
        Assert.Equal(60, total.Watts);
        Assert.Equal(["board", "package"], total.SensorIds);
    }

    [Fact]
    public void RyzenPackageAndDiscreteRadeonBothContribute()
    {
        var total = PowerTotals.Calculate([
            new("Ryzen 7 5800X", PowerDeviceKind.AmdCpu,
                [new("cpu", "CPU Package", 44.1), new("core", "Core0 (Windows)", 5.4, PowerSensorKind.WindowsCpuCore)]),
            new("AMD Radeon RX 9070 XT", PowerDeviceKind.AmdGpu, [new("gpu", "GPU Package", 50)]),
            new("AMD Radeon Graphics", PowerDeviceKind.AmdGpu, [new("apu", "GPU Package", 3)])
        ]);
        Assert.Equal(94.1, total.Watts);
        Assert.Equal(["cpu", "gpu"], total.SensorIds);
        Assert.False(total.IsCpuPartial);
    }

    [Fact]
    public void RyzenWithoutPackageUsesOneSetOfCoreMetersAndIncludesDiscreteRadeon()
    {
        var total = PowerTotals.Calculate([
            new("Ryzen 7 5800X", PowerDeviceKind.AmdCpu,
            [
                new("cpu", "CPU Package", null),
                new("smu-core1", "Core #1 (SMU)", 4.9, PowerSensorKind.HardwareCpuCore),
                new("emi-core0", "Core0 (Windows)", 5.4, PowerSensorKind.WindowsCpuCore),
                new("emi-core2", "Core2 (Windows)", 3.5, PowerSensorKind.WindowsCpuCore),
                new("emi-core4", "Core4 (Windows)", null, PowerSensorKind.WindowsCpuCore),
                new("platform", "CPU Platform", 90)
            ]),
            new("AMD Radeon RX 9070 XT", PowerDeviceKind.AmdGpu, [new("gpu", "GPU Package", 51)])
        ]);

        Assert.Equal(59.9, total.Watts);
        Assert.Equal(["emi-core0", "emi-core2", "gpu"], total.SensorIds);
        Assert.True(total.IsCpuPartial);
    }

    [Fact]
    public void MissingCpuReadingsMarkTheComponentSumAsPartial()
    {
        var total = PowerTotals.Calculate([
            new("CPU", PowerDeviceKind.AmdCpu, [new("cpu", "Package", null)]),
            new("GPU", PowerDeviceKind.Gpu, [new("gpu", "GPU Package", 51)])
        ]);

        Assert.Equal(51, total.Watts);
        Assert.True(total.IsCpuPartial);
    }

    [Fact]
    public void AmdPackageTrackingAndAggregateCorePowerTakePrecedenceOverIndividualCores()
    {
        var ppt = new PowerSensor("ppt", "CPU PPT", 45);
        var cores = new PowerSensor("cores", "Core Power", 30);
        var core = new PowerSensor("core", "Core0 (Windows)", 5, PowerSensorKind.WindowsCpuCore);

        var package = PowerTotals.Calculate([new("CPU", PowerDeviceKind.AmdCpu, [ppt, cores, core])]);
        Assert.Equal(45, package.Watts);
        Assert.Equal(["ppt"], package.SensorIds);
        Assert.False(package.IsCpuPartial);

        var fallback = PowerTotals.Calculate([new("CPU", PowerDeviceKind.AmdCpu, [cores, core])]);
        Assert.Equal(30, fallback.Watts);
        Assert.Equal(["cores"], fallback.SensorIds);
        Assert.True(fallback.IsCpuPartial);
    }


}
