using Dwell.Core;
using Xunit;

namespace Dwell.Core.Tests;

public sealed class PowerCoreNumbersTests
{
    [Fact]
    public void WindowsCoreIdsAreNumberedInNumericOrderIncludingMissingReadings()
    {
        int[] counters = [14, 4, 0, 12, 2, 6, 10, 8];
        var sensors = counters.Select(counter => new PowerSensor($"counter-{counter}",
            $"Core{counter} (Windows)", counter == 2 ? null : 4, PowerSensorKind.WindowsCpuCore)).ToArray();

        var numbers = PowerCoreNumbers.ForDevice(new("Ryzen 7 5800X", PowerDeviceKind.AmdCpu, sensors));

        Assert.Equal(8, numbers.Count);
        for (var index = 0; index < 8; index++)
            Assert.Equal(index + 1, numbers[$"counter-{index * 2}"]);
    }

    [Fact]
    public void ProvidersEachStartAtOneAndThePackageHasNoCoreNumber()
    {
        var device = new PowerDevice("Ryzen", PowerDeviceKind.AmdCpu,
        [
            new("package", "CPU Package", 48),
            new("smu-2", "Core #2 (SMU)", 2, PowerSensorKind.HardwareCpuCore),
            new("smu-1", "Core #1 (SMU)", 3, PowerSensorKind.HardwareCpuCore),
            new("windows-5", "Core5 (Windows)", 4, PowerSensorKind.WindowsCpuCore)
        ]);

        var numbers = PowerCoreNumbers.ForDevice(device);

        Assert.Equal(3, numbers.Count);
        Assert.Equal(1, numbers["smu-1"]);
        Assert.Equal(2, numbers["smu-2"]);
        Assert.Equal(1, numbers["windows-5"]);
        Assert.False(numbers.ContainsKey("package"));
    }
}
