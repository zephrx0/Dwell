using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dwell.Core;
using Dwell.Services;

namespace Dwell.ViewModels;

public sealed record TargetOption(string Key, string Name);
public sealed record AllowedApp(string Key, string Name)
{
    public string RemoveLabel => Strings.Get("Remove");
}

public sealed partial class LimitRowViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private UsageLimit _rule;
    private bool _initializing = true;
    public string Name => UsageProjection.TargetName(_rule.Target, _rule.Name);
    public string Usage { get; private set; } = "";
    public double Progress { get; private set; }
    public string LimitMinutesLabel => Strings.Get("LimitMinutes");
    public string RemoveLabel => Strings.Get("Remove");
    public string EnabledLabel => Strings.Get("Enabled");
    [ObservableProperty] public partial double Minutes { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; }
    public event Action? Changed;
    public event Action? SaveFailed;
    public LimitRowViewModel(UsageLimit rule, double seconds, SettingsService settings)
    {
        _rule = rule; _settings = settings; Minutes = rule.Minutes; Enabled = rule.Enabled;
        Usage = Strings.Format("LimitUsage", Strings.Duration(seconds), Strings.Duration(rule.Minutes * 60));
        Progress = Math.Clamp(seconds / (rule.Minutes * 60d) * 100, 0, 100);
        _initializing = false;
    }
    partial void OnMinutesChanged(double value)
    {
        if (_initializing || !double.IsFinite(value)) return;
        _rule = _rule with { Minutes = (int)Math.Clamp(value, 1, 1440) };
        Save();
    }
    partial void OnEnabledChanged(bool value) { if (_initializing) return; _rule = _rule with { Enabled = value }; Save(); }
    private void Save()
    {
        try { _settings.PutLimit(_rule); Changed?.Invoke(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SaveFailed?.Invoke(); }
    }
    [RelayCommand] private void Remove()
    {
        try { _settings.RemoveLimit(_rule.Id); Changed?.Invoke(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SaveFailed?.Invoke(); }
    }
}

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly TrackingService _tracker;
    private bool _initializing = true;
    private bool _loading;
    private bool _reload;
    [ObservableProperty] public partial int TargetIndex { get; set; }
    [ObservableProperty] public partial double NewMinutes { get; set; } = 60;
    [ObservableProperty] public partial int AllowIndex { get; set; }
    [ObservableProperty] public partial bool BreaksEnabled { get; set; }
    [ObservableProperty] public partial double BreakMinutes { get; set; }
    [ObservableProperty] public partial bool DowntimeEnabled { get; set; }
    [ObservableProperty] public partial TimeSpan DowntimeStart { get; set; }
    [ObservableProperty] public partial TimeSpan DowntimeEnd { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    public ObservableCollection<TargetOption> Targets { get; } = [];
    public ObservableCollection<TargetOption> UserApps { get; } = [];
    public ObservableCollection<LimitRowViewModel> Limits { get; } = [];
    public ObservableCollection<AllowedApp> Allowed { get; } = [];

    public RulesViewModel(SettingsService settings, TrackingService tracker)
    {
        _settings = settings; _tracker = tracker;
        var policy = settings.Policy();
        BreaksEnabled = policy.BreaksEnabled; BreakMinutes = policy.BreakMinutes;
        DowntimeEnabled = policy.DowntimeEnabled; DowntimeStart = TimeSpan.FromMinutes(policy.DowntimeStart); DowntimeEnd = TimeSpan.FromMinutes(policy.DowntimeEnd);
        _initializing = false;
    }

    partial void OnBreaksEnabledChanged(bool value) { if (_initializing) return; _settings.Current.BreaksEnabled = value; Save(); }
    partial void OnBreakMinutesChanged(double value) { if (_initializing || !double.IsFinite(value)) return; _settings.Current.BreakMinutes = (int)Math.Clamp(value, 5, 180); Save(); }
    partial void OnDowntimeEnabledChanged(bool value) { if (_initializing) return; _settings.Current.DowntimeEnabled = value; Save(); }
    partial void OnDowntimeStartChanged(TimeSpan value) { if (_initializing) return; _settings.Current.DowntimeStart = Math.Clamp((int)value.TotalMinutes, 0, 1439); Save(); }
    partial void OnDowntimeEndChanged(TimeSpan value) { if (_initializing) return; _settings.Current.DowntimeEnd = Math.Clamp((int)value.TotalMinutes, 0, 1439); Save(); }
    private void Save()
    {
        try { _settings.Save(); Error = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = Strings.Get("SettingsError"); }
    }

    [RelayCommand] private async Task AddLimitAsync()
    {
        if (TargetIndex < 0 || TargetIndex >= Targets.Count || !double.IsFinite(NewMinutes)) return;
        try
        {
            var target = Targets[TargetIndex];
            _settings.PutLimit(new(Guid.NewGuid(), target.Key, target.Name, (int)Math.Clamp(NewMinutes, 1, 1440)));
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = Strings.Get("SettingsError"); }
    }

    [RelayCommand] private async Task AllowAppAsync()
    {
        if (AllowIndex < 0 || AllowIndex >= UserApps.Count) return;
        try { _settings.SetAllowed(UserApps[AllowIndex].Key, true); await RefreshAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = Strings.Get("SettingsError"); }
    }
    [RelayCommand] private async Task RemoveAllowedAsync(AllowedApp app)
    {
        try { _settings.SetAllowed(app.Key, false); await RefreshAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = Strings.Get("SettingsError"); }
    }

    public async Task RefreshAsync()
    {
        if (_loading) { _reload = true; return; }
        _loading = true;
        try
        {
            do
            {
                _reload = false;
                var start = new DateTimeOffset(DateTime.Today);
                var result = await _tracker.ReadAsync(start, new DateTimeOffset(DateTime.Today.AddDays(1)));
                var groups = UsageProjection.Groups(result.Apps).OrderBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                var policy = _settings.Policy();
                var selected = TargetIndex >= 0 && TargetIndex < Targets.Count ? Targets[TargetIndex].Key : "all";
                var allowedSelection = AllowIndex >= 0 && AllowIndex < UserApps.Count ? UserApps[AllowIndex].Key : "";
                Targets.Clear(); Targets.Add(new("all", Strings.Get("WholePC")));
                foreach (var category in Enum.GetValues<AppCategory>()) Targets.Add(new("category:" + (int)category, Strings.Get("Category" + category)));
                UserApps.Clear();
                foreach (var group in groups)
                {
                    var name = ApplicationPolicy.DisplayName(group.App.Name);
                    Targets.Add(new("app:" + group.Key, name)); UserApps.Add(new(group.Key, name));
                }
                TargetIndex = Math.Max(0, Targets.ToList().FindIndex(x => x.Key == selected));
                AllowIndex = Math.Max(0, UserApps.ToList().FindIndex(x => x.Key == allowedSelection));
                var entries = UsageProjection.Entries(result, start, new DateTimeOffset(DateTime.Today.AddDays(1)));
                Limits.Clear();
                foreach (var rule in policy.Limits)
                {
                    var row = new LimitRowViewModel(rule, UsageProjection.TargetSeconds(rule.Target, entries, policy.AlwaysAllowed), _settings);
                    row.Changed += () => _ = RefreshAsync();
                    row.SaveFailed += () => Error = Strings.Get("SettingsError");
                    Limits.Add(row);
                }
                Allowed.Clear();
                foreach (var key in policy.AlwaysAllowed)
                {
                    var group = groups.FirstOrDefault(x => x.Key == key);
                    if (group is not null) Allowed.Add(new(key, ApplicationPolicy.DisplayName(group.App.Name)));
                }
                Error = "";
            } while (_reload);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { Error = Strings.Get("StorageError"); }
        finally { _loading = false; }
    }
}
