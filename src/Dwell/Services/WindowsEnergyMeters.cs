using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dwell.Services;

internal sealed record EnergyChannel(string Id, string Device, string Name, double? Watts);

// Windows EMI returns cumulative energy in pWh and timestamps in 100 ns ticks.
internal sealed class WindowsEnergyMeters : IDisposable
{
    private static readonly Guid InterfaceClass = new("45BD8344-7ED6-49CF-A440-C276C933B053");
    private const uint GetVersion = 0x224000, GetMetadataSize = 0x224004, GetMetadata = 0x224008, GetMeasurement = 0x22400c;
    private const int CounterSize = 16;
    private const long MaxSampleGap = TimeSpan.TicksPerMinute;
    private readonly List<Meter> _meters = [];
    private long _nextScan;

    private readonly record struct Counter(ulong Energy, ulong Time, long ReadAt);

    private sealed class Meter(string path, string name, SafeFileHandle handle, string?[] channels)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public SafeFileHandle Handle { get; } = handle;
        public string?[] Channels { get; } = channels;
        public byte[] Buffer { get; } = new byte[channels.Length * CounterSize];
        public Counter[] Previous { get; } = new Counter[channels.Length];
    }

    public List<EnergyChannel> Read()
    {
        if (Environment.TickCount64 >= _nextScan)
        {
            _nextScan = Environment.TickCount64 + 30_000;
            foreach (var path in FindInterfaces())
                if (!_meters.Any(x => x.Path == path) && Open(path) is { } meter) _meters.Add(meter);
        }

        var readings = new List<EnergyChannel>();
        for (var i = _meters.Count - 1; i >= 0; i--)
        {
            var meter = _meters[i];
            if (ReadControl(meter.Handle, GetMeasurement, meter.Buffer) < meter.Buffer.Length)
            {
                meter.Handle.Dispose();
                _meters.RemoveAt(i);
                _nextScan = Math.Min(_nextScan, Environment.TickCount64 + 2_000);
                continue;
            }

            var now = DateTime.UtcNow.Ticks;
            for (var channel = 0; channel < meter.Channels.Length; channel++)
            {
                if (meter.Channels[channel] is not { } name) continue;
                var offset = channel * CounterSize;
                var energy = BinaryPrimitives.ReadUInt64LittleEndian(meter.Buffer.AsSpan(offset));
                var time = BinaryPrimitives.ReadUInt64LittleEndian(meter.Buffer.AsSpan(offset + 8));
                ref var previous = ref meter.Previous[channel];
                var watts = UpdateCounter(ref previous, new Counter(energy, time, now));
                readings.Add(new($"emi:{meter.Path}/{channel}", meter.Name, name, watts));
            }
        }
        return readings;
    }

    private static double? UpdateCounter(ref Counter previous, Counter current)
    {
        // Keep the original baseline while a driver repeats the same measurement.
        if (current.Time == previous.Time) return null;
        var baseline = previous;
        previous = current;

        // A wall-clock gap also catches long suspend periods when the meter's clock stopped.
        var elapsed = current.ReadAt - baseline.ReadAt;
        if (elapsed is <= 0 or > MaxSampleGap || baseline.Time == 0 || current.Time < baseline.Time ||
            current.Energy < baseline.Energy || current.Time - baseline.Time > MaxSampleGap)
            return null;

        return 0.036 * (current.Energy - baseline.Energy) / (current.Time - baseline.Time);
    }

    private static string[] FindInterfaces()
    {
        var guid = InterfaceClass;
        if (CM_Get_Device_Interface_List_SizeW(out var length, ref guid, null, 0) != 0 || length is < 2 or > 65_536) return [];
        var paths = new char[length];
        return CM_Get_Device_Interface_ListW(ref guid, null, paths, length, 0) == 0
            ? new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    private static Meter? Open(string path)
    {
        var handle = CreateFileW(path, 0x80000000, 3, 0, 3, 0, 0);
        if (!handle.IsInvalid)
        {
            var versionBuffer = new byte[2];
            var sizeBuffer = new byte[4];
            var version = ReadControl(handle, GetVersion, versionBuffer) == versionBuffer.Length
                ? BinaryPrimitives.ReadUInt16LittleEndian(versionBuffer) : 0;
            if (version is 1 or 2 &&
                ReadControl(handle, GetMetadataSize, sizeBuffer) == sizeBuffer.Length &&
                BinaryPrimitives.ReadUInt32LittleEndian(sizeBuffer) is >= 68 and <= 16_384 and var size)
            {
                var metadata = new byte[size];
                var bytesRead = ReadControl(handle, GetMetadata, metadata);
                if (bytesRead >= 68 && bytesRead <= metadata.Length && ParseMetadata(metadata[..bytesRead], version) is { } parsed)
                    return new(path, parsed.Device, handle, parsed.Channels);
            }
        }
        handle.Dispose();
        return null;
    }

    private static (string Device, string?[] Channels)? ParseMetadata(byte[] metadata, int version)
    {
        if (version == 1)
        {
            if (metadata.Length < 72 || BinaryPrimitives.ReadUInt32LittleEndian(metadata) != 0) return null;
            var size = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(70));
            if (size < 2 || size % 2 != 0 || size > metadata.Length - 72) return null;
            return (ReadName(metadata, 36, 32), [ReadName(metadata, 72, size)]);
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(66));
        if (count is 0 or > 256) return null;
        var channels = new string?[count];
        var offset = 68;
        for (var i = 0; i < count; i++)
        {
            if (metadata.Length - offset < 6) return null;
            var unit = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(offset));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(offset + 4));
            offset += 6;
            if (size < 2 || size % 2 != 0 || size > metadata.Length - offset) return null;
            if (unit == 0) channels[i] = ReadName(metadata, offset, size);
            offset += size;
        }
        return (ReadName(metadata, 32, 32), channels);
    }

    private static string ReadName(byte[] data, int offset, int size) =>
        Encoding.Unicode.GetString(data, offset, size).Split('\0', 2)[0].Trim();

    private static int ReadControl(SafeFileHandle handle, uint code, byte[] output) =>
        DeviceIoControl(handle, code, 0, 0, output, output.Length, out var size, 0) ? size : 0;

    public void Dispose()
    {
        foreach (var meter in _meters) meter.Handle.Dispose();
        _meters.Clear();
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string? device, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string? device, [Out] char[] paths, uint length, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, int inputSize, [Out] byte[] output, int outputSize, out int returned, nint overlapped);
}
