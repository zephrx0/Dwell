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

public sealed record PowerSensorRow(string Device, string Sensor, string Value, string Included);
public sealed record PowerChartChoice(string? Id, string Label);

public sealed partial class PowerView : UserControl
{
    private const long BucketMilliseconds = 900_000;
    private const string BatteryId = "battery-discharge";
    private const string DiskActivityPrefix = "disk-activity:";
    private const string DiskEstimatePrefix = "disk-estimate:";
    private const string MissingValue = "\u2014";
    private readonly HardwarePowerMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<PowerReading> _points = [];
    private readonly Dictionary<string, string> _savedLabels = new(StringComparer.Ordinal);
    private readonly Axis _timeAxis = new() { MinLimit = -0.5 };
    private readonly Axis _valueAxis = new() { MinLimit = 0 };
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
        Chart.YAxes = [_valueAxis];
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
            var sensors = sample.Devices.SelectMany(device => device.Sensors
                .Where(sensor => sensor.Watts.HasValue)
                .Select(sensor => (sensor, Label: sensor.Kind == PowerSensorKind.EstimatedDiskPower
                    ? device.Name : $"{device.Name} / {sensor.Name}"))).DistinctBy(x => x.sensor.Id).ToArray();
            var watts = sensors.ToDictionary(x => x.sensor.Id, x => x.sensor.Watts!.Value, StringComparer.Ordinal);
            var labels = sensors.Where(x => _savedLabels.GetValueOrDefault(x.sensor.Id) != x.Label)
                .ToDictionary(x => x.sensor.Id, x => x.Label, StringComparer.Ordinal);
            var activity = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var disk in sample.DiskActivity)
            {
                if (disk.ActivePercent is not (>= 0 and <= 100)) continue;
                activity[disk.Id] = disk.ActivePercent.Value;
                if (_savedLabels.GetValueOrDefault(disk.Id) != disk.Name) labels[disk.Id] = disk.Name;
            }
            if (sample.BatteryWatts is { } battery) watts[BatteryId] = battery;
            var reading = new PowerReading(now, sample.Total, watts, activity.Count == 0 ? null : activity);
            try
            {
                var previous = await Task.Run(() => PowerUsageHistory.Save(reading, labels, !_loadedHistory));
                if (!_loadedHistory)
                {
                    var recent = _points.Select(x => x.At).ToHashSet();
                    _points.AddRange(previous.Readings.Where(x => !recent.Contains(x.At)));
                    foreach (var (id, label) in previous.Labels) _savedLabels[id] = label;
                    _loadedHistory = true;
                }
                foreach (var (id, label) in labels) _savedLabels[id] = label;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { System.Diagnostics.Trace.WriteLine(ex); }
            if (!_timer.IsEnabled) return;
            _points.Add(reading);
            _points.RemoveAll(x => x.At < now - PowerUsageHistory.WindowMilliseconds || x.At > now);
            if (_visible) RenderSample();
        }
        finally { _reading = false; }
    }

    internal void Localize()
    {
        Heading.Text = Strings.Get("PowerPageTitle");
        SensorsHeading.Text = Strings.Get("PowerSensors");
        DeviceHeading.Text = Strings.Get("PowerDevice");
        ValueHeading.Text = Strings.Get("PowerValue");
        IncludedHeading.Text = Strings.Get("PowerIncluded");
        ChartSource.Header = Strings.Get("PowerChartSource");
        RenderSample();
    }

    private void RenderSample()
    {
        var total = _sample?.Total ?? PowerTotal.Unavailable;
        Total.Text = FormatWatts(total.Watts, total.IsEstimated);
        Scope.Text = Strings.Get(total.IsCpuPartial ? "PowerSourcePartialCpu" :
            total.IsEstimated ? "PowerSourceEstimatedDisks" : "PowerSource" + total.Source);
        if (total.IsCpuPartial && total.IsEstimated) Scope.Text += " " + Strings.Get("PowerSourceEstimatedDisks");
        WaitMessage.Text = Strings.Get(_sample is null ? "PowerReading" : "PowerNoReadings");
        var included = total.SensorIds.ToHashSet(StringComparer.Ordinal);
        var yes = Strings.Get("PowerYes");
        var rows = new List<PowerSensorRow>();
        var chartIds = _points.SelectMany(x => x.Watts.Keys.Concat(
            x.DiskActivity?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())).ToHashSet(StringComparer.Ordinal);
        var choices = new List<PowerChartChoice> { new(null, Strings.Get("PowerChartTotal")) };
        if (chartIds.Remove(BatteryId))
            choices.Add(new(BatteryId, Strings.Get("PowerDischarge")));
        if (_sample?.BatteryWatts is { } battery)
        {
            rows.Add(new(Strings.Get("PowerBattery"), Strings.Get("PowerDischarge"), FormatWatts(battery), total.Source == PowerSource.Battery ? yes : ""));
        }
        foreach (var device in _sample?.Devices ?? [])
        {
            if (device.Sensors.Length == 0)
                rows.Add(new(device.Name, Strings.Get("PowerNoSensor"), MissingValue, ""));
            foreach (var sensor in device.Sensors)
            {
                var estimated = sensor.Kind == PowerSensorKind.EstimatedDiskPower;
                var name = estimated ? Strings.Get("PowerDiskEstimate") : sensor.Name;
                rows.Add(new(device.Name, name, FormatWatts(sensor.Watts, estimated), included.Contains(sensor.Id) ? yes : ""));
                if (chartIds.Remove(sensor.Id))
                    choices.Add(new(sensor.Id, $"{device.Name} / {name}"));
            }
        }
        foreach (var disk in _sample?.DiskActivity ?? [])
        {
            rows.Add(new(disk.Name, Strings.Get("PowerDiskActivity"), FormatPercent(disk.ActivePercent), ""));
            if (chartIds.Remove(disk.Id))
                choices.Add(new(disk.Id, ChartLabel(disk.Id, disk.Name)));
        }
        choices.AddRange(chartIds.Select(id => new PowerChartChoice(id, ChartLabel(id, _savedLabels.GetValueOrDefault(id, id)))));
        Sensors.ItemsSource = rows.OrderBy(x => x.Value == MissingValue).ToArray();
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
        var selected = ChartSource.SelectedItem as PowerChartChoice ?? _choices[0];
        var activity = IsDiskActivity(selected.Id);
        WindowLabel.Text = Strings.Get("PowerWindow");
        if (_choices.Any(choice => IsDiskEstimate(choice.Id)))
            WindowLabel.Text += "\n" + Strings.Get("PowerDiskEstimateNote");
        if (_points.Count == 0)
        {
            Chart.Visibility = Visibility.Collapsed;
            WaitMessage.Visibility = Visibility.Visible;
            return;
        }
        _valueAxis.MaxLimit = activity ? 100d : null;
        _valueAxis.MinStep = activity ? 25 : 1;
        _valueAxis.Labeler = value => value.ToString("0.#", Strings.Culture) + (activity ? " %" : " W");
        const int count = (int)(PowerUsageHistory.WindowMilliseconds / BucketMilliseconds) + 1;
        var firstBucket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / BucketMilliseconds - count + 1;
        var bucketed = _points.ToLookup(x => (int)(x.At / BucketMilliseconds - firstBucket));
        var ticks = Enumerable.Range(0, count).ToArray();
        string Label(double index) => DateTimeOffset.FromUnixTimeMilliseconds((firstBucket + (long)index) * BucketMilliseconds)
            .ToLocalTime().ToString("HH:mm", Strings.Culture);
        _timeAxis.MaxLimit = count - 0.5;
        _timeAxis.CustomSeparators = ticks.Where(x => (firstBucket + x) % 12 == 0).Select(x => (double)x).ToArray();
        _timeAxis.Labeler = Label;
        Rectangle[] colors = [Swatch0, Swatch1, Swatch2, Swatch3, Swatch4];
        var averages = ticks.Select(x => bucketed[x].Average(p => p.Total.Watts)).ToArray();
        var estimatedBuckets = ticks.Select(x => bucketed[x].Any(p => p.Total.IsEstimated)).ToArray();
        bool IsEstimatedBucket(double index) => (int)index >= 0 && (int)index < ticks.Length && estimatedBuckets[(int)index];
        string TooltipLabel(double index)
        {
            var text = Label(index);
            if (selected.Id is null && (int)index >= 0 && (int)index < ticks.Length && averages[(int)index] is { } average)
                text += $"   {selected.Label} {FormatWatts(average, IsEstimatedBucket(index))}";
            return text;
        }
        ISeries Series(string name, int color, double?[] values, string? id) => new StackedColumnSeries<double?>
        {
            Name = name,
            Values = values,
            Fill = Paint(colors[color % colors.Length]),
            StackGroup = 0, MaxBarWidth = 24, Padding = 1, Rx = 0, Ry = 0,
            XToolTipLabelFormatter = point => TooltipLabel(point.Coordinate.SecondaryValue),
            YToolTipLabelFormatter = point => $"{name}  {(activity
                ? FormatPercent(point.Coordinate.PrimaryValue)
                : FormatWatts(point.Coordinate.PrimaryValue,
                    IsDiskEstimate(id) || id is null && IsEstimatedBucket(point.Coordinate.SecondaryValue)))}"
        };
        double? SensorValue(PowerReading sample, string id)
        {
            var values = activity ? sample.DiskActivity : sample.Watts;
            return values is not null && values.TryGetValue(id, out var value) ? value : null;
        }
        if (selected.Id is { } id)
            Chart.Series = [Series(selected.Label, 0, ticks.Select(x => bucketed[x].Average(p => SensorValue(p, id))).ToArray(), id)];
        else
        {
            var ids = _points.SelectMany(x => x.Total.SensorIds).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (ids.Length > 0 && _points.All(x => x.Total.Source is PowerSource.Components or PowerSource.None))
                Chart.Series = ids.Select((sensorId, i) => Series(
                    _choices.FirstOrDefault(x => x.Id == sensorId)?.Label ?? sensorId, i,
                    ticks.Select(x => Contribution(bucketed[x], sensorId)).ToArray(), sensorId)).ToArray();
            else
                Chart.Series = [Series(selected.Label, 0, averages, null)];
        }

        var hasHistory = selected.Id is { } selectedId
            ? _points.Any(x => SensorValue(x, selectedId).HasValue)
            : _points.Any(x => x.Total.Watts.HasValue);
        Chart.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        WaitMessage.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double? Contribution(IEnumerable<PowerReading> bucket, string id)
    {
        var measured = 0;
        double? sum = null;
        foreach (var reading in bucket)
        {
            if (reading.Total.Watts is null) continue;
            measured++;
            if (reading.Total.SensorIds.Contains(id) && reading.Watts.TryGetValue(id, out var watts))
                sum = (sum ?? 0) + watts;
        }
        return sum / measured;
    }

    private void ApplyChartTheme()
    {
        var text = Paint(AxisSwatch);
        _timeAxis.LabelsPaint = text;
        _valueAxis.LabelsPaint = text;
        _valueAxis.SeparatorsPaint = Paint(GridSwatch);
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

    private static string FormatWatts(double? watts, bool estimated = false) => watts is { } value
        ? (estimated ? "≈ " : "") + value.ToString("N1", Strings.Culture) + " W"
        : MissingValue;

    private static string FormatPercent(double? percent) =>
        percent is { } value ? value.ToString("N1", Strings.Culture) + " %" : MissingValue;

    private static string ChartLabel(string id, string label)
    {
        if (IsDiskActivity(id)) return $"{label} / {Strings.Get("PowerDiskActivity")}";
        if (IsDiskEstimate(id)) return $"{label} / {Strings.Get("PowerDiskEstimate")}";
        return label;
    }

    private static bool IsDiskActivity(string? id) => id?.StartsWith(DiskActivityPrefix, StringComparison.Ordinal) == true;

    private static bool IsDiskEstimate(string? id) => id?.StartsWith(DiskEstimatePrefix, StringComparison.Ordinal) == true;

}
