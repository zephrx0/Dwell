using System.Text.Json;

namespace Dwell.Services;

public enum ReminderKind { Limit, Downtime, Break }
public sealed record UsageReminder(ReminderKind Kind, UsageLimit? Limit = null, int Minutes = 0);

public sealed class ReminderService(TrackingService tracker, SettingsService settings) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Task? _worker;
    private DateOnly _day;
    private HashSet<string> _sent = [];
    private double _lastBreakSeconds;
    private readonly string _path = Path.Combine(SettingsService.DataDirectory, "reminders.json");
    public event Action<UsageReminder>? Reminder;

    public void Start() => _worker = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) await CheckAsync(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    });

    public static bool InDowntime(TimeSpan now, int start, int end)
    {
        var minute = (int)now.TotalMinutes;
        return start != end && (start < end ? minute >= start && minute < end : minute >= start || minute < end);
    }

    public async Task CheckAsync()
    {
        if (!await _checking.WaitAsync(0)) return;
        try
        {
            var status = tracker.Status;
            if (status.ContinuousSeconds < _lastBreakSeconds) _lastBreakSeconds = 0;
            if (!status.IsActive || status.IsPaused) return;
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (_day != today)
            {
                _day = today; _sent = _sent.Where(x => x == "rest:" + today || x == "rest:" + today.AddDays(-1)).ToHashSet(StringComparer.Ordinal);
                try
                {
                    var saved = JsonSerializer.Deserialize<NotificationState>(File.ReadAllText(_path));
                    if (saved?.Day == today) _sent = (saved.Sent ?? []).ToHashSet(StringComparer.Ordinal);
                    else if (saved?.Day == today.AddDays(-1)) _sent.UnionWith((saved.Sent ?? []).Where(x => x == "rest:" + today.AddDays(-1)));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            var policy = settings.Policy();
            if (status.AppKey is { } key && !policy.AlwaysAllowed.Contains(key))
            {
                if (policy.DowntimeEnabled && InDowntime(DateTime.Now.TimeOfDay, policy.DowntimeStart, policy.DowntimeEnd))
                {
                    // The date belongs to the start of an overnight interval.
                    var windowDate = DateTime.Now.TimeOfDay.TotalMinutes < policy.DowntimeEnd && policy.DowntimeStart > policy.DowntimeEnd ? today.AddDays(-1) : today;
                    if (Mark("rest:" + windowDate)) { Reminder?.Invoke(new(ReminderKind.Downtime)); return; }
                }
                var limits = policy.Limits.Where(x => x.Enabled).ToArray();
                if (limits.Length > 0 && status.App is { Ignored: false } current)
                {
                    var currentGroup = new AppGroup(key, current, [current]);
                    var relevant = limits.Where(x => UsageProjection.Matches(x.Target, currentGroup)).OrderBy(x => x.Target == "all" ? 2 : x.Target.StartsWith("category:", StringComparison.Ordinal) ? 1 : 0).ToArray();
                    if (relevant.Length > 0)
                    {
                        var start = new DateTimeOffset(DateTime.Today);
                        var end = new DateTimeOffset(DateTime.Today.AddDays(1));
                        var result = await tracker.ReadAsync(start, end);
                        var entries = UsageProjection.Entries(result, start, end);
                        foreach (var limit in relevant)
                            if (UsageProjection.TargetSeconds(limit.Target, entries, policy.AlwaysAllowed) >= limit.Minutes * 60 && Mark("limit:" + limit.Id + ":" + limit.Minutes))
                            { Reminder?.Invoke(new(ReminderKind.Limit, limit)); return; }
                    }
                }
            }
            if (policy.BreaksEnabled && status.ContinuousSeconds - _lastBreakSeconds >= policy.BreakMinutes * 60)
            {
                _lastBreakSeconds = status.ContinuousSeconds;
                Reminder?.Invoke(new(ReminderKind.Break, Minutes: policy.BreakMinutes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        finally { _checking.Release(); }
    }

    private bool Mark(string key)
    {
        if (!_sent.Add(key)) return false;
        try { File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new NotificationState(_day, _sent.ToArray()))); File.Move(_path + ".tmp", _path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return true;
    }
    private sealed record NotificationState(DateOnly Day, string[] Sent);
    public void Dispose() { _stop.Cancel(); _worker?.GetAwaiter().GetResult(); _stop.Dispose(); _checking.Dispose(); }
}
