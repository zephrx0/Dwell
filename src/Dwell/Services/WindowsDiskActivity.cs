using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Dwell.Core;

namespace Dwell.Services;

internal sealed record DiskActivity(string Id, string Name, double? ActivePercent);
internal sealed record DiskSample(uint Index, string Id, string Name, DiskLoad? Load, double? EstimatedWatts);

internal sealed class WindowsDiskActivity
{
    private sealed record Disk(uint Index, string Id, string Name, DiskPowerProfile? Profile);
    private Disk[] _disks = [];
    private Dictionary<string, DiskCounters> _previous = new(StringComparer.Ordinal);
    private long _refreshAt;

    public DiskSample[] Read()
    {
        if (Environment.TickCount64 >= _refreshAt)
        {
            _refreshAt = Environment.TickCount64 + 60_000;
            _disks = ReadInventory() ?? _disks;
        }
        if (_disks.Length == 0) return [];

        var counters = ReadCounters();
        var current = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        var samples = new DiskSample[_disks.Length];
        for (var i = 0; i < _disks.Length; i++)
        {
            var disk = _disks[i];
            DiskLoad? load = null;
            if (counters.TryGetValue(disk.Index, out var counter))
            {
                current[disk.Id] = counter;
                if (_previous.TryGetValue(disk.Id, out var previous)) load = counter.Since(previous);
            }
            samples[i] = new(disk.Index, disk.Id, disk.Name, load,
                load is { } measured ? disk.Profile?.Estimate(measured) : null);
        }
        _previous = current;
        return samples;
    }

    private static Disk[]? ReadInventory()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT Index, Model, PNPDeviceID FROM Win32_DiskDrive");
            using var results = query.Get();
            var disks = new List<Disk>();
            foreach (ManagementObject disk in results)
            {
                using (disk)
                {
                    if (disk["Index"] is not uint index || index == uint.MaxValue ||
                        disk["Model"] is not string model || string.IsNullOrWhiteSpace(model)) continue;
                    var name = model.Trim();
                    var identity = disk["PNPDeviceID"] as string;
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}|{identity ?? index.ToString(CultureInfo.InvariantCulture)}"));
                    disks.Add(new(index, Convert.ToHexString(hash.AsSpan(0, 12)), name, DiskPowerProfile.ForModel(name)));
                }
            }
            return disks.OrderBy(x => x.Index).ToArray();
        }
        catch (Exception ex) when (WmiError(ex)) { return null; }
    }

    private static Dictionary<uint, DiskCounters> ReadCounters()
    {
        var counters = new Dictionary<uint, DiskCounters>();
        try
        {
            using var query = new ManagementObjectSearcher("""
                SELECT Name, PercentIdleTime, PercentIdleTime_Base, PercentDiskReadTime, PercentDiskWriteTime
                FROM Win32_PerfRawData_PerfDisk_PhysicalDisk
                """);
            using var results = query.Get();
            foreach (ManagementObject row in results)
            {
                using (row)
                {
                    if (row["Name"] is not string name) continue;
                    var split = name.IndexOf(' ');
                    var ordinal = split < 0 ? name.AsSpan() : name.AsSpan(0, split);
                    if (!uint.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                        row["PercentIdleTime_Base"] is not ulong time || time == 0 ||
                        row["PercentIdleTime"] is not ulong idle || row["PercentDiskReadTime"] is not ulong read ||
                        row["PercentDiskWriteTime"] is not ulong write) continue;
                    counters[index] = new(time, idle, read, write);
                }
            }
        }
        catch (Exception ex) when (WmiError(ex)) { }
        return counters;
    }

    private static bool WmiError(Exception ex) => ex is ManagementException or UnauthorizedAccessException
        or System.Runtime.InteropServices.COMException or System.Security.SecurityException;
}
