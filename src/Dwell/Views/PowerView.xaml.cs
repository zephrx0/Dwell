using Dwell.Core;
using Dwell.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SkiaSharp;

namespace Dwell.Views;

public sealed record PowerSensorRow(string Device, string Sensor, string Wattage, string Included);
public sealed record PowerChartChoice(string? Id, string Label);

public sealed partial class PowerView : UserControl
{
    private const long WindowMilliseconds = 86_400_000;
    private const long BucketMilliseconds = 900_000;
    private const string BatteryId = "battery-discharge";
    private sealed record Reading(long At, PowerTotal Total, Dictionary<string, double> Watts);
    private readonly HardwarePowerMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Queue<Reading> _points = new();
    private readonly Axis _timeAxis = new() { MinLimit = -0.5 };
    private readonly Axis _wattsAxis = new() { MinLimit = 0, MinStep = 1 };
    private PowerChartChoice[] _choices = [];
    private bool _updatingChoices;
    private HardwarePowerSample? _sample;
    private bool _visible;
    private bool _loadedHistory;
    private bool _reading;

    public PowerView()
    {
        InitializeComponent();
        Chart.XAxes = [_timeAxis];
        Chart.YAxes = [_wattsAxis];
        _wattsAxis.Labeler = value => value.ToString("0.#", Strings.Culture) + " W";
        _timer.Tick += async (_, _) => await ReadAsync();
        Loaded += (_, _) => ApplyChartTheme();
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyChartTheme);
        Localize();
    }

    internal void Start()
    {
        _visible = true;
        RenderSample();
    }

    internal void BeginRecording()
    {
        _timer.Start();
        _ = ReadAsync();
    }

    internal void Stop()
    {
        _visible = false;
    }

    internal void Close()
    {
        _timer.Stop();
        _ = Task.Run(_monitor.Dispose);
    }

    private async Task ReadAsync()
    {
        if (_reading || !_timer.IsEnabled) return;
        _reading = true;
        try
        {
            var sample = await Task.Run(_monitor.Read);
            if (!_timer.IsEnabled) return;
            _sample = sample;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var watts = sample.Devices.SelectMany(x => x.Sensors).Where(x => x.Watts.HasValue)
                .DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => x.Watts!.Value, StringComparer.Ordinal);
            if (sample.BatteryWatts is { } battery) watts[BatteryId] = battery;
            var reading = new Reading(now, sample.Total, watts);
            try
            {
                var previous = await Task.Run(() => Store(reading, !_loadedHistory));
                if (!_loadedHistory)
                {
                    var recent = _points.ToArray();
                    _points.Clear();
                    foreach (var point in previous.Concat(recent)) _points.Enqueue(point);
                    _loadedHistory = true;
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { System.Diagnostics.Trace.WriteLine(ex); }
            if (!_timer.IsEnabled) return;
            _points.Enqueue(reading);
            while (_points.Peek().At < now - WindowMilliseconds) _points.Dequeue();
            if (_visible) RenderSample();
        }
        finally { _reading = false; }
    }

    private static List<Reading> Store(Reading reading, bool load)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = System.IO.Path.Combine(SettingsService.DataDirectory, "power.db") }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS samples(time INTEGER PRIMARY KEY, data TEXT NOT NULL); DELETE FROM samples WHERE time < $cutoff";
        command.Parameters.AddWithValue("$cutoff", reading.At - WindowMilliseconds);
        command.ExecuteNonQuery();
        var previous = new List<Reading>();
        if (load)
        {
            command.CommandText = "SELECT data FROM samples WHERE time < $now ORDER BY time";
            command.Parameters.AddWithValue("$now", reading.At);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (System.Text.Json.JsonSerializer.Deserialize<Reading>(reader.GetString(0)) is { } point) previous.Add(point);
        }
        command.CommandText = "INSERT OR REPLACE INTO samples VALUES ($time, $data)";
        command.Parameters.AddWithValue("$time", reading.At);
        command.Parameters.AddWithValue("$data", System.Text.Json.JsonSerializer.Serialize(reading));
        command.ExecuteNonQuery();
        transaction.Commit();
        return previous;
    }

    internal void Localize()
    {
        Heading.Text = Strings.Get("PowerPageTitle");
        SensorsHeading.Text = Strings.Get("PowerSensors");
        DeviceHeading.Text = Strings.Get("PowerDevice");
        WattsHeading.Text = Strings.Get("PowerWatts");
        IncludedHeading.Text = Strings.Get("PowerIncluded");
        WindowLabel.Text = Strings.Get("PowerWindow");
        ChartSource.Header = Strings.Get("PowerChartSource");
        RenderSample();
    }

    private void RenderSample()
    {
        var total = _sample?.Total ?? PowerTotal.Unavailable;
        Total.Text = FormatWatts(total.Watts);
        Scope.Text = Strings.Get("PowerSource" + total.Source);
        WaitMessage.Text = Strings.Get(_sample is null ? "PowerReading" : "PowerNoReadings");
        var included = total.SensorIds.ToHashSet(StringComparer.Ordinal);
        var yes = Strings.Get("PowerYes");
        var rows = new List<PowerSensorRow>();
        var choices = new List<PowerChartChoice> { new(null, Strings.Get("PowerChartTotal")) };
        if (_sample?.BatteryWatts is { } battery)
        {
            rows.Add(new(Strings.Get("PowerBattery"), Strings.Get("PowerDischarge"), FormatWatts(battery), total.Source == PowerSource.Battery ? yes : ""));
            choices.Add(new(BatteryId, Strings.Get("PowerDischarge")));
        }
        foreach (var device in _sample?.Devices ?? [])
        {
            if (device.Sensors.Length == 0)
                rows.Add(new(device.Name, Strings.Get("PowerNoSensor"), FormatWatts(null), ""));
            foreach (var sensor in device.Sensors)
            {
                rows.Add(new(device.Name, sensor.Name, FormatWatts(sensor.Watts), included.Contains(sensor.Id) ? yes : ""));
                if (sensor.Watts.HasValue || _points.Any(x => x.Watts.ContainsKey(sensor.Id)))
                    choices.Add(new(sensor.Id, $"{device.Name} / {sensor.Name}"));
            }
        }
        Sensors.ItemsSource = rows.OrderBy(x => x.Wattage == FormatWatts(null)).ToArray();
        if (!_choices.SequenceEqual(choices))
        {
            var selectedId = (ChartSource.SelectedItem as PowerChartChoice)?.Id;
            _updatingChoices = true;
            _choices = choices.ToArray();
            ChartSource.ItemsSource = _choices;
            ChartSource.SelectedItem = _choices.FirstOrDefault(x => x.Id == selectedId) ?? _choices[0];
            ChartSource.IsEnabled = _choices.Length > 1;
            _updatingChoices = false;
        }
        RenderChart();
    }

    private void ChartSourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingChoices) RenderChart();
    }

    private void RenderChart()
    {
        if (_choices.Length == 0) return;
        if (_points.Count == 0)
        {
            Chart.Visibility = Visibility.Collapsed;
            WaitMessage.Visibility = Visibility.Visible;
            return;
        }
        var selected = ChartSource.SelectedItem as PowerChartChoice ?? _choices[0];
        const int count = (int)(WindowMilliseconds / BucketMilliseconds);
        var firstBucket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / BucketMilliseconds - count + 1;
        var bucketed = _points.ToLookup(x => (int)(x.At / BucketMilliseconds - firstBucket));
        var ticks = Enumerable.Range(0, count).ToArray();
        string Label(double index) => DateTimeOffset.FromUnixTimeMilliseconds((firstBucket + (long)index) * BucketMilliseconds)
            .ToLocalTime().ToString("HH:mm", Strings.Culture);
        _timeAxis.MaxLimit = count - 0.5;
        _timeAxis.CustomSeparators = ticks.Where(x => (firstBucket + x) % 12 == 0).Select(x => (double)x).ToArray();
        _timeAxis.Labeler = Label;
        Rectangle[] colors = [Swatch0, Swatch1, Swatch2, Swatch3, Swatch4];
        ISeries Series(string name, int color, Func<Reading, double?> value) => new StackedColumnSeries<double?>
        {
            Name = name,
            Values = ticks.Select(x => bucketed[x].Average(value)).ToArray(),
            Fill = Paint(colors[color % colors.Length]),
            StackGroup = 0, MaxBarWidth = 24, Padding = 1, Rx = 0, Ry = 0,
            XToolTipLabelFormatter = point => Label(point.Coordinate.SecondaryValue),
            YToolTipLabelFormatter = point => $"{name}  {FormatWatts(point.Coordinate.PrimaryValue)}"
        };
        static double? SensorValue(Reading sample, string id) => sample.Watts.TryGetValue(id, out var value) ? value : null;
        if (selected.Id is { } id)
            Chart.Series = [Series(selected.Label, 0, x => SensorValue(x, id))];
        else
        {
            var ids = _points.SelectMany(x => x.Total.SensorIds).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (ids.Length > 0 && _points.All(x => x.Total.Source is PowerSource.Components or PowerSource.None))
                Chart.Series = ids.Select((sensorId, i) => Series(
                    _choices.FirstOrDefault(x => x.Id == sensorId)?.Label ?? sensorId, i,
                    x => x.Total.Watts is null ? null : x.Total.SensorIds.Contains(sensorId) ? SensorValue(x, sensorId) : 0)).ToArray();
            else
                Chart.Series = [Series(selected.Label, 0, x => x.Total.Watts)];
        }

        var hasHistory = selected.Id is { } selectedId
            ? _points.Any(x => x.Watts.ContainsKey(selectedId))
            : _points.Any(x => x.Total.Watts.HasValue);
        Chart.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        WaitMessage.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyChartTheme()
    {
        var text = Paint(AxisSwatch);
        _timeAxis.LabelsPaint = text;
        _wattsAxis.LabelsPaint = text;
        _wattsAxis.SeparatorsPaint = Paint(GridSwatch);
        Chart.TooltipBackgroundPaint = Paint(TooltipBackgroundSwatch);
        Chart.TooltipTextPaint = Paint(TooltipTextSwatch);
        RenderChart();
    }

    private static SolidColorPaint Paint(Rectangle swatch)
    {
        var brush = (SolidColorBrush)swatch.Fill;
        var color = brush.Color;
        return new SolidColorPaint(new SKColor(color.R, color.G, color.B, (byte)Math.Round(color.A * brush.Opacity))) { StrokeThickness = 1 };
    }

    private static string FormatWatts(double? watts) =>
        watts is { } value ? value.ToString("N1", Strings.Culture) + " W" : "\u2014";

}
