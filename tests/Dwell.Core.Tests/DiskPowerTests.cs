using Dwell.Core;
using Xunit;

namespace Dwell.Core.Tests;

public sealed class DiskPowerTests
{
    [Fact]
    public void CalculatesBusyTimeFromIdleEvenWhenReadAndWriteRequestsOverlap()
    {
        var previous = new DiskCounters(10_000_000, 7_000_000, 4_000_000, 3_000_000);
        var current = new DiskCounters(20_000_000, 12_000_000, 12_000_000, 11_000_000);

        var load = Assert.IsType<DiskLoad>(current.Since(previous));
        Assert.Equal(0.5, load.ActiveFraction);
        Assert.Equal(0.5, load.ReadFraction);
        var seagate = Assert.IsType<DiskPowerProfile>(DiskPowerProfile.ForModel("ST500VT000-1DK142"));
        Assert.Equal(1.1, seagate.Estimate(load), 6);
    }

    [Fact]
    public void DropsCounterResetsAndLeavesUnratedDriveWattageUnavailable()
    {
        var previous = new DiskCounters(10_000_000, 7_000_000, 4_000_000, 3_000_000);
        Assert.Null(new DiskCounters(20_000_000, 1, 1, 1).Since(previous));
        Assert.Null(DiskPowerProfile.ForModel("ADATA SU650"));
    }

    [Fact]
    public void MarksComponentTotalEstimatedAndKeepsWholeSystemReadingsAuthoritative()
    {
        var devices = new PowerDevice[]
        {
            new("CPU", PowerDeviceKind.Cpu, [new("cpu", "CPU Package", 24)]),
            new("HDD", PowerDeviceKind.Independent,
                [new("missing", "Drive Rail", null), new("disk-estimate:hdd", "Estimate", 4.6, PowerSensorKind.EstimatedDiskPower)])
        };
        var total = PowerTotals.Calculate(devices, null);
        Assert.True(total.IsEstimated);
        Assert.Equal(28.6, total.Watts);
        Assert.Equal(["cpu", "disk-estimate:hdd"], total.SensorIds);

        var battery = PowerTotals.Calculate(devices, 40);
        Assert.False(battery.IsEstimated);
        Assert.Equal(40, battery.Watts);
    }
}
