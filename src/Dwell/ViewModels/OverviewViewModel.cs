using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dwell.Core;
using Dwell.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Dwell.ViewModels;

public sealed record CategoryItem(AppCategory Category, string Name, string Time, double Seconds);
public sealed record MetricItem(string Label, string Value);
public sealed record PowerMetricItem(string Label, string Value, string Glyph);

public sealed partial class AppUsageViewModel : ObservableObject
{
    private readonly AppRepository _repository;
    private bool _initializing = true;
    private readonly IReadOnlyList<long> _appIds;
    public AppRecord App { get; }
    public string Name => ApplicationPolicy.DisplayName(App.Name);
    [ObservableProperty] public partial string ScreenTime { get; set; } = "";
    [ObservableProperty] public partial string BackgroundTime { get; set; } = "";
    [ObservableProperty] public partial string Cpu { get; set; } = "";
    [ObservableProperty] public partial string Memory { get; set; } = "";
    [ObservableProperty] public partial bool HasResources { get; set; }
    public string CpuLabel => Strings.Get("PeakCpu");
    public string MemoryLabel => Strings.Get("PeakRam");
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    public bool HasIcon => !string.IsNullOrEmpty(App.Icon) || Path.GetFileName(App.Exe).Equals("Dwell.exe", StringComparison.OrdinalIgnoreCase);
    public string? Icon => Path.GetFileName(App.Exe).Equals("Dwell.exe", StringComparison.OrdinalIgnoreCase)
        ? "ms-appx:///Assets/Dwell.png" : string.IsNullOrEmpty(App.Icon) ? null : new Uri(App.Icon).AbsoluteUri;
    public string CategoryLabel => Strings.Get("CategoryLabel.Text");
    public string CategoryActionLabel => Strings.Get("ChangeCategoryLabel");
    public string HistoryLabel => Strings.Get("ViewSessions");
    public string CategoryMenuLabel => Strings.Format("CategoryMenu", CategoryNames.ElementAtOrDefault(CategoryIndex) ?? CategoryNames[4]);
    public IReadOnlyList<string> CategoryNames { get; } = Enum.GetValues<AppCategory>().Select(x => Strings.Get("Category" + x)).ToArray();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryMenuLabel))]
    public partial int CategoryIndex { get; set; }
    public event Action? CategoryChanged;
    public event Action<string>? HistoryRequested;
    [RelayCommand] private void ViewHistory() => HistoryRequested?.Invoke(Name);
    [RelayCommand] private void SetCategory(string value) { if (int.TryParse(value, out var category) && category is >= 0 and <= 4) CategoryIndex = category; }

    public AppUsageViewModel(IReadOnlyList<AppRecord> apps, double seconds, double backgroundSeconds, ResourcePeak? peak, AppRepository repository)
    {
        App = apps.MaxBy(x => x.Id)!;
        _appIds = apps.Select(x => x.Id).ToArray();
        UpdateUsage(seconds, backgroundSeconds, peak);
        _repository = repository; CategoryIndex = (int)App.Category;
        _initializing = false;
    }

    internal bool Matches(IReadOnlyList<AppRecord> apps) =>
        _appIds.SequenceEqual(apps.Select(x => x.Id)) && CategoryIndex == (int)apps.MaxBy(x => x.Id)!.Category;

    internal void UpdateUsage(double seconds, double backgroundSeconds, ResourcePeak? peak)
    {
        ScreenTime = Strings.Format("ScreenTimeRow", Strings.Duration(seconds));
        BackgroundTime = Strings.Format("BackgroundTimeRow", Strings.Duration(backgroundSeconds));
        HasResources = peak is not null;
        Cpu = peak is null ? Strings.Get("NotMeasured") : peak.Cpu.ToString("0.#", Strings.Culture) + "%";
        Memory = peak is null ? Strings.Get("NotMeasured") : Strings.Format("MemoryMb", Math.Round(peak.Memory / 1048576d));
    }

    partial void OnCategoryIndexChanged(int value)
    {
        if (_initializing || value < 0 || value > 4) return;
        _repository.UpdateCategory(_appIds, (AppCategory)value);
        CategoryChanged?.Invoke();
    }
}

public sealed partial class OverviewViewModel(TrackingService tracker, AppRepository repository) : ObservableObject
{
    private DateTime _date = DateTime.Today;
    private DateTime _lastToday = DateTime.Today;
    private bool _loading;
    private bool _reload;
    private double _total;
    private readonly List<AppUsageViewModel> _allApps = [];
    private int _visibleCount = 8;
    private string? _rowLanguage;
    [ObservableProperty] public partial int PeriodIndex { get; set; } = 1;
    [ObservableProperty] public partial string PeriodTitle { get; set; } = "";
    [ObservableProperty] public partial string TotalTime { get; set; } = Strings.Duration(0);
    [ObservableProperty] public partial string AverageTime { get; set; } = "";
    [ObservableProperty] public partial string SummaryCaption { get; set; } = "";
    [ObservableProperty] public partial string Comparison { get; set; } = "";
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial bool CanGoForward { get; set; }
    [ObservableProperty] public partial string CurrentPeriodLabel { get; set; } = "";
    [ObservableProperty] public partial bool CanShowMore { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial double[][] Buckets { get; set; } = Enumerable.Range(0, 5).Select(_ => new double[7]).ToArray();
    [ObservableProperty] public partial double AverageSeconds { get; set; }
    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public ObservableCollection<AppUsageViewModel> Apps { get; } = [];
    public ObservableCollection<MetricItem> Metrics { get; } = [];
    [ObservableProperty] public partial bool HasApps { get; set; }
    [ObservableProperty] public partial PowerMetricItem[] PowerMetrics { get; set; } = [];
    public bool IsWeek => PeriodIndex == 0;
    public event Action? ChartChanged;
    public event Action<string, DateTime, bool>? AppHistoryRequested;

    partial void OnPeriodIndexChanged(int value) { _visibleCount = 8; OnPropertyChanged(nameof(IsWeek)); _ = RefreshAsync(); }

    [RelayCommand] private async Task PreviousAsync() { _date = _date.AddDays(IsWeek ? -7 : -1); _visibleCount = 8; await RefreshAsync(); }
    [RelayCommand] private async Task NextAsync() { if (!CanGoForward) return; _date = _date.AddDays(IsWeek ? 7 : 1); _visibleCount = 8; await RefreshAsync(); }
    [RelayCommand] private async Task CurrentPeriodAsync()
    {
        _date = DateTime.Today;
        _visibleCount = 8;
        await RefreshAsync();
    }
    [RelayCommand] private void ShowMore() { _visibleCount += 10; UpdateVisibleApps(); }

    public async Task RefreshAsync()
    {
        if (_date == _lastToday) _date = DateTime.Today;
        _lastToday = DateTime.Today;
        if (_loading) { _reload = true; return; }
        _loading = true;
        try
        {
            do
            {
                _reload = false;
                var week = IsWeek;
                var date = _date;
                var startDate = week ? date.AddDays(-((int)date.DayOfWeek + 6) % 7) : date;
                var endDate = startDate.AddDays(week ? 7 : 1);
                var start = new DateTimeOffset(startDate);
                var end = new DateTimeOffset(endDate);
                var result = await tracker.ReadAsync(start, end);
                var backgroundResult = await tracker.ReadAsync(start, end, backgroundOnly: true);
                var previousStartDate = startDate.AddDays(week ? -7 : -1);
                var previousStart = new DateTimeOffset(previousStartDate);
                var elapsed = DateTime.Now < endDate ? DateTime.Now - startDate : endDate - startDate;
                var previousEnd = new DateTimeOffset(previousStartDate.Add(elapsed));
                var previous = await tracker.ReadAsync(previousStart, previousEnd);
                var power = await Task.Run(() => PowerHistory.Read(start, end));
                if (week != IsWeek || date != _date) { _reload = true; continue; }
                PeriodTitle = week ? Strings.Format("DateRange", startDate.ToString("d MMM", Strings.Culture), endDate.AddDays(-1).ToString("d MMM yyyy", Strings.Culture)) : date.ToString("d MMMM yyyy", Strings.Culture);
                string Count(int? count) => count?.ToString(Strings.Culture) ?? Strings.Get("NotMeasured");
                PowerMetrics =
                [
                    new(Strings.Get("PcSleep"), Count(power?.Sleeps), "\uE708"),
                    new(Strings.Get("PcOff"), Count(power?.Shutdowns), "\uE7E8"),
                    new(Strings.Get("PcRestart"), Count(power?.Restarts), "\uE72C")
                ];
                CanGoForward = endDate <= DateTime.Today;
                CurrentPeriodLabel = Strings.Get(week ? "CurrentWeek" : "Today");
                var buckets = Enumerable.Range(0, 5).Select(_ => new double[week ? 7 : 24]).ToArray();
                var appSeconds = new Dictionary<long, double>();
                var groups = UsageProjection.Groups(result.Apps).Select(x => x.Variants).ToArray();
                var appsById = groups.SelectMany(group => group.Select(app => (app.Id, Representative: group.MaxBy(x => x.Id)!))).ToDictionary(x => x.Id, x => x.Representative);
                var groupsById = groups.ToDictionary(group => group.MaxBy(x => x.Id)!.Id);
                var now = DateTimeOffset.UtcNow;
                var entries = UsageProjection.Entries(result, start, end);
                foreach (var session in result.Sessions)
                {
                    if (!appsById.TryGetValue(session.AppId, out var app) || app.Ignored) continue;
                    var from = session.Start > start ? session.Start : start;
                    var until = session.End < end ? session.End : end;
                    if (until > now) until = now;
                    if (until <= from) continue;
                    appSeconds[app.Id] = appSeconds.GetValueOrDefault(app.Id) + (until - from).TotalSeconds;
                    // UTC hour boundaries preserve elapsed time through daylight saving changes.
                    while (from < until)
                    {
                        var local = from.LocalDateTime;
                        var nextHour = from.AddTicks(TimeSpan.TicksPerHour - local.Ticks % TimeSpan.TicksPerHour);
                        var boundary = nextHour < until ? nextHour : until;
                        var bucket = week ? (local.Date - startDate).Days : local.Hour;
                        if (bucket >= 0 && bucket < buckets[0].Length)
                            buckets[(int)app.Category][bucket] += (boundary - from).TotalSeconds;
                        from = boundary;
                    }
                }
                Buckets = buckets;
                _total = appSeconds.Values.Sum();
                var days = week ? Math.Clamp((DateTime.Today - startDate).Days + 1, 1, 7) : 1;
                AverageSeconds = _total / days;
                TotalTime = Strings.Duration(_total);
                AverageTime = Strings.Format("DailyAverage", Strings.Duration(AverageSeconds));
                SummaryCaption = Strings.Get(week ? "PeriodWeek" : "PeriodDay");
                var previousSeconds = UsageProjection.Entries(previous, previousStart, previousEnd).Sum(x => x.Seconds);
                Comparison = previousSeconds > 0 ? Strings.Format(_total >= previousSeconds ? "ComparedMore" : "ComparedLess", Math.Round(Math.Abs(_total - previousSeconds) / previousSeconds * 100), Strings.Get(week ? "PreviousWeek" : "PreviousDay")) : "";
                HasData = _total > 0;
                Metrics.Clear();
                Metrics.Add(new(Strings.Get("ActiveApps"), appSeconds.Count.ToString(Strings.Culture)));
                Metrics.Add(new(Strings.Get("Sessions"), entries.Length.ToString(Strings.Culture)));
                Metrics.Add(new(Strings.Get("LongestSession"), Strings.Duration(entries.Select(x => x.Seconds).DefaultIfEmpty(0).Max())));
                var peakIndex = Enumerable.Range(0, buckets[0].Length).MaxBy(i => buckets.Sum(x => x[i]));
                Metrics.Add(new(Strings.Get(week ? "PeakDay" : "PeakHour"), !HasData ? Strings.Get("EmptyState.Text") : week ? startDate.AddDays(peakIndex).ToString("ddd", Strings.Culture) : peakIndex.ToString("00") + ":00"));
                Categories.Clear();
                foreach (var category in Enum.GetValues<AppCategory>())
                {
                    var seconds = buckets[(int)category].Sum();
                    if (seconds > 0) Categories.Add(new(category, Strings.Get("Category" + category), Strings.Duration(seconds), seconds));
                }
                var existingRows = _allApps.ToDictionary(x => ApplicationPolicy.Identity(x.App.Exe, x.App.Name));
                var sameLanguage = _rowLanguage == Strings.Culture.Name;
                _rowLanguage = Strings.Culture.Name;
                var backgroundEntries = UsageProjection.Entries(backgroundResult, start, end);
                var backgroundSeconds = backgroundEntries.GroupBy(x => x.Group.Key).ToDictionary(x => x.Key, x => x.Sum(e => e.Seconds));
                var screenSeconds = entries.GroupBy(x => x.Group.Key).ToDictionary(x => x.Key, x => x.Sum(e => e.Seconds));
                var peaks = repository.ResourcePeaks();
                var keys = screenSeconds.Keys.Union(backgroundSeconds.Keys).ToHashSet();
                _allApps.Clear();
                foreach (var group in UsageProjection.Groups(result.Apps).Where(x => keys.Contains(x.Key))
                    .OrderByDescending(x => screenSeconds.GetValueOrDefault(x.Key)).ThenByDescending(x => backgroundSeconds.GetValueOrDefault(x.Key)))
                {
                    var groupPeaks = group.Variants.Select(x => peaks.GetValueOrDefault(x.Id)).Where(x => x is not null).ToArray();
                    var peak = groupPeaks.Length == 0 ? null : new ResourcePeak(groupPeaks.Max(x => x!.Cpu), groupPeaks.Max(x => x!.Memory));
                    var screen = screenSeconds.GetValueOrDefault(group.Key);
                    var background = backgroundSeconds.GetValueOrDefault(group.Key);
                    var previousRow = existingRows.GetValueOrDefault(group.Key);
                    AppUsageViewModel row;
                    if (sameLanguage && previousRow is not null && previousRow.Matches(group.Variants))
                    {
                        row = previousRow;
                        row.UpdateUsage(screen, background, peak);
                    }
                    else
                    {
                        row = new(group.Variants, screen, background, peak, repository) { IsExpanded = previousRow?.IsExpanded ?? false };
                        row.CategoryChanged += () => _ = RefreshAsync();
                        row.HistoryRequested += name => AppHistoryRequested?.Invoke(name, _date, IsWeek);
                    }
                    _allApps.Add(row);
                }
                HasApps = _allApps.Count > 0;
                UpdateVisibleApps();
                Error = tracker.Error is null ? "" : Strings.Get("StorageError");
                ChartChanged?.Invoke();
            } while (_reload);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { Error = Strings.Get("StorageError"); }
        finally { _loading = false; }
    }

    private void UpdateVisibleApps()
    {
        var desired = _allApps.Take(_visibleCount).ToArray();
        for (var i = Apps.Count - 1; i >= 0; i--)
            if (!desired.Contains(Apps[i])) Apps.RemoveAt(i);
        for (var i = 0; i < desired.Length; i++)
        {
            var current = Apps.IndexOf(desired[i]);
            if (current < 0) Apps.Insert(i, desired[i]);
            else if (current != i) Apps.Move(current, i);
        }
        CanShowMore = _allApps.Count > _visibleCount;
    }
}
