using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Dwell.Services;

namespace Dwell.ViewModels;

public sealed record ActivityRow(ActivityEntry Entry, bool IsWeek)
{
    public string Name => Dwell.Core.ApplicationPolicy.DisplayName(Entry.Group.App.Name);
    public string Category => Strings.Get("Category" + Entry.Group.App.Category);
    public string Time => Entry.Start.LocalDateTime.ToString(IsWeek ? "dd MMM HH:mm:ss" : "HH:mm:ss", Strings.Culture) + " - " + Entry.End.LocalDateTime.ToString(Entry.Start.LocalDateTime.Date != Entry.End.LocalDateTime.Date ? "dd MMM HH:mm:ss" : "HH:mm:ss", Strings.Culture);
    public string Duration => Entry.Seconds < 60 ? Strings.Format("Seconds", Math.Max(1, Math.Round(Entry.Seconds))) : Strings.Duration(Entry.Seconds);
}

public sealed partial class ActivityViewModel(TrackingService tracker) : ObservableObject
{
    private ActivityEntry[] _entries = [];
    private bool _loading;
    private bool _reload;
    private DateTime _lastToday = DateTime.Today;

    internal void FollowToday()
    {
        var previous = _lastToday;
        _lastToday = DateTime.Today;
        if (previous != _lastToday && Date?.LocalDateTime.Date == previous)
            Date = new DateTimeOffset(_lastToday);
    }
    [ObservableProperty] public partial DateTimeOffset? Date { get; set; } = DateTimeOffset.Now;
    [ObservableProperty] public partial int PeriodIndex { get; set; } = 1;
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial bool HasRows { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    public bool CanExport => HasRows && !IsBusy;
    partial void OnHasRowsChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    public ObservableCollection<ActivityRow> Rows { get; } = [];

    partial void OnDateChanged(DateTimeOffset? value) { _ = RefreshAsync(); }
    partial void OnPeriodIndexChanged(int value) { _ = RefreshAsync(); }
    partial void OnQueryChanged(string value) => Filter();
    public void ShowApp(string name, DateTime date, bool week) { Query = name; PeriodIndex = week ? 0 : 1; Date = new DateTimeOffset(date); _ = RefreshAsync(); }

    public async Task RefreshAsync()
    {
        if (_loading) { _reload = true; return; }
        _loading = true;
        IsBusy = true;
        try
        {
            do
            {
                _reload = false;
                var selected = (Date?.LocalDateTime ?? DateTime.Today).Date;
                var period = PeriodIndex;
                if (period == 0) selected = selected.AddDays(-((int)selected.DayOfWeek + 6) % 7);
                var start = new DateTimeOffset(selected);
                var end = new DateTimeOffset(selected.AddDays(period == 0 ? 7 : 1));
                var anchor = Date;
                var result = await tracker.ReadAsync(start, end);
                if (anchor != Date || period != PeriodIndex) { _reload = true; continue; }
                _entries = UsageProjection.Entries(result, start, end);
                Error = ""; Filter();
            } while (_reload);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { Error = Strings.Get("StorageError"); }
        finally { _loading = false; IsBusy = false; }
    }

    private void Filter()
    {
        Rows.Clear();
        foreach (var entry in _entries)
        {
            var row = new ActivityRow(entry, PeriodIndex == 0);
            if (string.IsNullOrWhiteSpace(Query) || row.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) || row.Category.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)) Rows.Add(row);
        }
        HasRows = Rows.Count > 0;
        Summary = Strings.Format("HistorySummary", Rows.Count, Strings.Duration(Rows.Sum(x => x.Entry.Seconds)));
    }

    public async Task ExportAsync(string path)
    {
        var rows = Rows.ToArray();
        var separator = Strings.Culture.TextInfo.ListSeparator;
        var headers = new[] { Strings.Get("CsvApp"), Strings.Get("CsvCategory"), Strings.Get("CsvStart"), Strings.Get("CsvEnd"), Strings.Get("CsvSeconds") };
        static string Escape(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { string.Join(separator, headers.Select(Escape)) };
        lines.AddRange(rows.Select(x => string.Join(separator, new[] { x.Name, x.Category, x.Entry.Start.ToLocalTime().ToString("O", CultureInfo.InvariantCulture), x.Entry.End.ToLocalTime().ToString("O", CultureInfo.InvariantCulture), x.Entry.Seconds.ToString("0.###", Strings.Culture) }.Select(Escape))));
        await File.WriteAllTextAsync(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
    }
}
