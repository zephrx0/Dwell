using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace Dwell.Services;

internal sealed record PowerHistory(int Sleeps, int Shutdowns, int Restarts)
{
    public static PowerHistory? Read(DateTimeOffset start, DateTimeOffset end)
    {
        var from = start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var until = end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var query = $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=506 or EventID=109) and TimeCreated[@SystemTime >= '{from}' and @SystemTime < '{until}']]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            var sleeps = 0;
            var shutdowns = 0;
            var restarts = 0;
            DateTime? lastShutdown = null;
            int lastShutdownId = 0;
            void CountShutdown(EventRecord entry)
            {
                var time = entry.TimeCreated;
                if (time is { } current && lastShutdown is { } previous && entry.Id != lastShutdownId &&
                    current - previous is { Ticks: >= 0 and <= TimeSpan.TicksPerSecond * 10 })
                {
                    lastShutdown = null;
                    return;
                }
                shutdowns++;
                lastShutdown = time;
                lastShutdownId = entry.Id;
            }
            using var action = new EventLogPropertySelector(["Event/EventData/Data[@Name='ShutdownActionType']"]);
            using var target = new EventLogPropertySelector(["Event/EventData/Data[@Name='TargetState']"]);
            while (true)
            {
                using var entry = reader.ReadEvent();
                if (entry is null) break;
                if (entry.Id == 506) sleeps++;
                else if (entry.Id == 42 && entry is EventLogRecord transition)
                {
                    // Fast startup uses hibernation internally with PowerSystemShutdown as its target.
                    if (Convert.ToInt32(transition.GetPropertyValues(target)[0], CultureInfo.InvariantCulture) == 6) CountShutdown(entry);
                    else sleeps++;
                }
                else if (entry is EventLogRecord record)
                {
                    switch (Convert.ToInt32(record.GetPropertyValues(action)[0], CultureInfo.InvariantCulture))
                    {
                        case 4 or 6: CountShutdown(entry); break;
                        case 5: restarts++; break;
                    }
                }
            }
            return new(sleeps, shutdowns, restarts);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
