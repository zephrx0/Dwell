namespace Dwell.Core;

public readonly record struct DiskCounters(ulong Time, ulong IdleTime, ulong ReadTime, ulong WriteTime)
{
    public DiskLoad? Since(DiskCounters previous)
    {
        if (Time <= previous.Time || Time - previous.Time > TimeSpan.TicksPerMinute ||
            IdleTime < previous.IdleTime || ReadTime < previous.ReadTime || WriteTime < previous.WriteTime)
            return null;

        var elapsed = (double)(Time - previous.Time);
        var active = 1 - Math.Clamp((IdleTime - previous.IdleTime) / elapsed, 0, 1);
        // Reads and writes can overlap. Divide the actual busy time by their relative service times.
        var read = (double)(ReadTime - previous.ReadTime);
        var write = (double)(WriteTime - previous.WriteTime);
        return new(active, read + write > 0 ? read / (read + write) : 0.5);
    }
}

public readonly record struct DiskLoad(double ActiveFraction, double ReadFraction);

public readonly record struct DiskPowerProfile(double IdleWatts, double ReadWatts, double WriteWatts)
{
    public static DiskPowerProfile? ForModel(string name)
    {
        // Match the rated model, ignoring a Windows vendor prefix and the drive's revision suffix.
        var model = name.Trim().ToUpperInvariant();
        if (model.StartsWith("WDC ", StringComparison.Ordinal)) model = model[4..];
        if (model.EndsWith(" ATA DEVICE", StringComparison.Ordinal)) model = model[..^11];
        return model.Split('-', 2)[0] switch
        {
            "ST500DM002" => new(4.6, 6.19, 6.19), // Seagate desktop HDD DS1770, typical operating mix
            "ST500VT000" => new(0.70, 1.30, 1.70), // Seagate Video 2.5 HDD 100713733, active idle
            "KINGSTON SA400S37120G" or "KINGSTON SA400S37/120G" => new(0.195, 0.642, 1.535), // A400 datasheet, max R/W
            "WD40EZAX" => new(3.1, 4.7, 4.7), // WD Blue 3.5-inch SATA PC HDD datasheet
            _ => null
        };
    }

    public double Estimate(DiskLoad load)
    {
        var activeWatts = ReadWatts * load.ReadFraction + WriteWatts * (1 - load.ReadFraction);
        return IdleWatts * (1 - load.ActiveFraction) + activeWatts * load.ActiveFraction;
    }
}
