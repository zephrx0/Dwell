using System.Text.Json;
using Microsoft.Win32;

namespace Dwell.Services;

public sealed record UsageLimit(Guid Id, string Target, string Name, int Minutes, bool Enabled = true);
public sealed record PolicySettings(UsageLimit[] Limits, HashSet<string> AlwaysAllowed, bool BreaksEnabled, int BreakMinutes, bool DowntimeEnabled, int DowntimeStart, int DowntimeEnd);

public sealed class UserSettings
{
    public int Theme { get; set; }
    public int IdleMinutes { get; set; } = 3;
    public int Language { get; set; }
    public bool AutoStart { get; set; } = true;
    public bool TrackingPaused { get; set; }
    public List<UsageLimit> Limits { get; set; } = [];
    public List<string> AlwaysAllowed { get; set; } = [];
    public bool BreaksEnabled { get; set; }
    public int BreakMinutes { get; set; } = 45;
    public bool DowntimeEnabled { get; set; }
    public int DowntimeStart { get; set; } = 22 * 60;
    public int DowntimeEnd { get; set; } = 8 * 60;
}

public sealed class SettingsService
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dwell");
    private readonly string _path = Path.Combine(DataDirectory, "settings.json");
    private readonly object _sync = new();
    public UserSettings Current { get; }

    public SettingsService()
    {
        Directory.CreateDirectory(DataDirectory);
        try { Current = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { Current = new(); }
        Current.IdleMinutes = Math.Clamp(Current.IdleMinutes, 1, 60);
        Current.Theme = Math.Clamp(Current.Theme, 0, 2);
        Current.Language = Math.Clamp(Current.Language, 0, 2);
        Current.Limits ??= [];
        Current.Limits = Current.Limits.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Target)).DistinctBy(x => x.Target)
            .Select(x => x with { Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id, Minutes = Math.Clamp(x.Minutes, 1, 1440) }).ToList();
        Current.AlwaysAllowed ??= [];
        Current.BreakMinutes = Math.Clamp(Current.BreakMinutes, 5, 180);
        Current.DowntimeStart = Math.Clamp(Current.DowntimeStart, 0, 1439);
        Current.DowntimeEnd = Math.Clamp(Current.DowntimeEnd, 0, 1439);
    }

    public void Save()
    {
        lock (_sync)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current));
            File.Move(temporary, _path, true);
        }
    }

    public PolicySettings Policy()
    {
        lock (_sync) return new(Current.Limits.ToArray(), Current.AlwaysAllowed.ToHashSet(StringComparer.Ordinal), Current.BreaksEnabled, Current.BreakMinutes, Current.DowntimeEnabled, Current.DowntimeStart, Current.DowntimeEnd);
    }

    public void PutLimit(UsageLimit limit)
    {
        lock (_sync)
        {
            var previous = Current.Limits;
            Current.Limits = previous.Where(x => x.Id != limit.Id && x.Target != limit.Target).Append(limit with { Minutes = Math.Clamp(limit.Minutes, 1, 1440) }).ToList();
            try { Save(); } catch { Current.Limits = previous; throw; }
        }
    }
    public void RemoveLimit(Guid id)
    {
        lock (_sync) { var previous = Current.Limits; Current.Limits = previous.Where(x => x.Id != id).ToList(); try { Save(); } catch { Current.Limits = previous; throw; } }
    }
    public void SetAllowed(string key, bool allowed)
    {
        lock (_sync)
        {
            var previous = Current.AlwaysAllowed; Current.AlwaysAllowed = previous.Where(x => x != key).ToList(); if (allowed) Current.AlwaysAllowed.Add(key);
            try { Save(); } catch { Current.AlwaysAllowed = previous; throw; }
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool AutoStart
    {
        get => Current.AutoStart;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Dwell", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("Dwell", false);
            Current.AutoStart = value;
            Save();
        }
    }

    public void SynchronizeAutoStart() => AutoStart = Current.AutoStart;
}
