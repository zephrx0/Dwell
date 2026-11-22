using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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

public sealed record PowerSensorRow(string Device, string Sensor, string Wattage, string Included, string MeasurementDetail = "")
{
    public Visibility DeviceVisibility => Device.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class PowerProcessorRow(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] public partial string Sensor { get; set; } = "";
    [ObservableProperty] public partial string Wattage { get; set; } = "";
    [ObservableProperty] public partial string Included { get; set; } = "";
    [ObservableProperty] public partial string MeasurementDetail { get; set; } = "";
    [ObservableProperty] public partial PowerSensorRow[] Details { get; set; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
}

public sealed record PowerChartChoice(string? Id, string Label);

public sealed partial class PowerView : UserControl
{
    private const long BucketMilliseconds = 900_000;
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(2);
    private readonly PowerWorkerClient _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = BackgroundInterval };
    private readonly List<PowerReading> _points = [];
    private readonly Dictionary<string, string> _savedLabels = new(StringComparer.Ordinal);
    private readonly ObservableCollection<PowerProcessorRow> _processors = [];
    private readonly Axis _timeAxis = new() { MinLimit = -0.5 };
    private readonly Axis _wattsAxis = new() { MinLimit = 0, MinStep = 1 };
    private PowerChartChoice[] _choices = [];
    private bool _updatingChoices;
    private HardwarePowerSample? _sample;
    private bool _visible;
    private bool _loadedHistory;
    private bool _reading;
    private long _lastSampleTime;

    public PowerView()
    {
        InitializeComponent();
        Processors.ItemsSource = _processors;
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
        if (!_visible) _timer.Interval = LiveInterval;
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
        if (_visible) _timer.Interval = BackgroundInterval;
        _visible = false;
    }

    internal void Close()
    {
        _timer.Stop();
        _monitor.Dispose();
    }

    private async Task ReadAsync()
    {
        if (_reading || !_timer.IsEnabled) return;
        _reading = true;
        try
        {
            var sample = await _monitor.ReadAsync();
            if (!_timer.IsEnabled) return;
            if (sample is null)
            {
                _sample = null;
                _lastSampleTime = 0;
                if (_visible) { RenderSample(); }
                return;
            }
            _sample = sample;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sampleTime = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastSampleTime, sampleTime).TotalSeconds;
            var duration = _lastSampleTime != 0 && elapsed is > 0 and <= 10 ? elapsed : _timer.Interval.TotalSeconds;
            _lastSampleTime = sampleTime;
            var sensors = sample.Devices.SelectMany(device => device.Sensors
                .Where(sensor => sensor.Watts.HasValue)
                .Select(sensor => (sensor, Label: $"{device.Name} / {sensor.Name}"))).DistinctBy(x => x.sensor.Id).ToArray();
            var watts = sensors.ToDictionary(x => x.sensor.Id, x => x.sensor.Watts!.Value, StringComparer.Ordinal);
            var labels = sensors.Where(x => _savedLabels.GetValueOrDefault(x.sensor.Id) != x.Label)
                .ToDictionary(x => x.sensor.Id, x => x.Label, StringComparer.Ordinal);
            var reading = new PowerReading(now, sample.Total, watts, CpuGpuOnly: true, DurationSeconds: duration);
            try
            {
                var previous = await Task.Run(() => PowerUsageHistory.Save(reading, labels, !_loadedHistory));
                if (!_loadedHistory)
                {
                    var recent = _points.Select(x => x.At).ToHashSet();
                    var available = sample.Devices.SelectMany(x => x.Sensors).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                    _points.AddRange(previous.Readings.Where(x => !recent.Contains(x.At))
                        .Select(x => x.ForCpuAndGpu(available, previous.Labels)));
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
        WattsHeading.Text = Strings.Get("PowerWatts");
        IncludedHeading.Text = Strings.Get("PowerIncluded");
        CpuPowerHelp.Content = Strings.Get("PowerCpuHelp");
        CpuPowerHelp.NavigateUri = new Uri("https://github.com/zephrx0/Dwell#" +
            (Strings.Culture.TwoLetterISOLanguageName == "ru" ? "энергопотребление" : "power-usage"));
        WindowLabel.Text = Strings.Get("PowerWindow");
        ChartSource.Header = Strings.Get("PowerChartSource");
        RenderSample();
    }

    private void RenderSample()
    {
        var total = _sample?.Total ?? PowerTotal.Unavailable;
        Total.Text = FormatWatts(total.Watts);
        Scope.Text = Strings.Get(total.IsCpuPartial ? "PowerSourcePartialCpu" : "PowerSource" + total.Source);
        if (_sample?.GpuPollingPaused == true) Scope.Text += "\n" + Strings.Get("PowerGpuSuspended");
        WaitMessage.Text = Strings.Get(_sample is null ? "PowerReading" : "PowerNoReadings");
        var included = total.SensorIds.ToHashSet(StringComparer.Ordinal);
        var yes = Strings.Get("PowerYes");
        var rows = new List<PowerSensorRow>();
        var chartIds = _points.SelectMany(x => x.Watts.Keys).ToHashSet(StringComparer.Ordinal);
        var choices = new List<PowerChartChoice> { new(null, Strings.Get("PowerChartTotal")) };
        var processorIndex = 0;
        var cpuPowerUnavailable = false;
        foreach (var device in _sample?.Devices ?? [])
        {
            var isProcessor = device.Kind is PowerDeviceKind.Cpu or PowerDeviceKind.AmdCpu;
            if (isProcessor)
                cpuPowerUnavailable |= UpdateProcessor(device, processorIndex++, included, yes);
            else if (device.Sensors.Length == 0)
                rows.Add(new(device.Name, Strings.Get("PowerNoSensor"), FormatWatts(null), ""));
            foreach (var sensor in device.Sensors)
            {
                if (!isProcessor)
                    rows.Add(new(device.Name, SensorLabel(sensor.Name), FormatWatts(sensor.Watts),
                        included.Contains(sensor.Id) ? yes : "", MeasurementDetail(sensor)));
                if (chartIds.Remove(sensor.Id))
                    choices.Add(new(sensor.Id, $"{device.Name} / {SensorLabel(sensor.Name)}"));
            }
        }
        CpuPowerHelp.Visibility = cpuPowerUnavailable && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.X64 ? Visibility.Visible : Visibility.Collapsed;
        if (_sample is not null)
        {
            while (_processors.Count > processorIndex) _processors.RemoveAt(_processors.Count - 1);
        }
        else
        {
            foreach (var processor in _processors)
            {
                processor.Sensor = Strings.Get("PowerReading");
                processor.Wattage = FormatWatts(null);
                processor.Included = "";
                processor.MeasurementDetail = "";
                processor.Details = [];
            }
        }
        choices.AddRange(chartIds.Select(id => new PowerChartChoice(id, LocalizeSavedLabel(_savedLabels.GetValueOrDefault(id, id)))));
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

    private bool UpdateProcessor(PowerDevice device, int index, HashSet<string> included, string yes)
    {
        if (index == _processors.Count) _processors.Add(new(device.Name));
        else if (_processors[index].Name != device.Name) _processors[index] = new(device.Name);
        var row = _processors[index];
        var selected = device.Sensors.Where(sensor => included.Contains(sensor.Id)).DistinctBy(sensor => sensor.Id).ToArray();
        var package = selected.FirstOrDefault(sensor => sensor.Name is "CPU Package" or "Package" or "CPU PPT");
        row.Sensor = Strings.Get(package is not null ? "PowerProcessorPackage" :
            selected.Length > 0 ? "PowerProcessorCores" : "PowerCpuNoWatts");
        if (selected.Length == 0 && device.LoadPercent is { } load)
            row.Sensor += " " + Strings.Format("PowerCpuLoad", Math.Round(load));
        if (package?.AverageSeconds is { } seconds)
            row.Sensor += ", " + Strings.Format("PowerAverageSeconds", seconds);
        row.MeasurementDetail = selected.Length == 1 ? MeasurementDetail(selected[0]) : "";
        row.Wattage = FormatWatts(selected.Length > 0 ? selected.Sum(sensor => sensor.Watts) : null);
        row.Included = selected.Length > 0 ? yes : "";
        row.Details = device.Sensors
            .Where(sensor => sensor.Watts.HasValue && sensor.Id != package?.Id)
            .OrderBy(sensor => sensor.Name, StringComparer.Create(Strings.Culture, CompareOptions.NumericOrdering))
            .Select(sensor => new PowerSensorRow("", SensorLabel(sensor.Name), FormatWatts(sensor.Watts), "", MeasurementDetail(sensor)))
            .ToArray();
        return selected.Length == 0;
    }

    private static string MeasurementDetail(PowerSensor sensor)
    {
        if (sensor.Watts is null) return "";
        return sensor.AverageSeconds is { } seconds
            ? Strings.Format("PowerWindowsMeterDetail", sensor.Name, seconds)
            : Strings.Format("PowerHardwareMeterDetail", sensor.Name);
    }

    private static string SensorLabel(string name) => name switch
    {
        "CPU Package" or "Package" => Strings.Get("PowerProcessorPackage"),
        "CPU PPT" => Strings.Get("PowerProcessorPpt"),
        "GPU Package" or "GPU Power" => Strings.Get("PowerGraphicsPower"),
        "GPU Total" or "GPU Board Power" => Strings.Get("PowerGraphicsBoard"),
        _ => name
    };

    private static string LocalizeSavedLabel(string label)
    {
        var split = label.LastIndexOf(" / ", StringComparison.Ordinal);
        return split < 0 ? label : label[..(split + 3)] + SensorLabel(label[(split + 3)..]);
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
        var averages = ticks.Select(x => PowerReading.Average(bucketed[x], p => p.Total.Watts)).ToArray();
        string TooltipLabel(double index)
        {
            var text = Label(index);
            if (selected.Id is null && (int)index >= 0 && (int)index < ticks.Length && averages[(int)index] is { } average)
                text += $"   {selected.Label} {FormatWatts(average)}";
            return text;
        }
        ISeries Series(string name, int color, double?[] values) => new StackedColumnSeries<double?>
        {
            Name = name,
            Values = values,
            Fill = Paint(colors[color % colors.Length]),
            StackGroup = 0, MaxBarWidth = 24, Padding = 1, Rx = 0, Ry = 0,
            XToolTipLabelFormatter = point => TooltipLabel(point.Coordinate.SecondaryValue),
            YToolTipLabelFormatter = point => $"{name}  {FormatWatts(point.Coordinate.PrimaryValue)}"
        };
        static double? SensorValue(PowerReading sample, string id) => sample.Watts.TryGetValue(id, out var value) ? value : null;
        if (selected.Id is { } id)
            Chart.Series = [Series(selected.Label, 0, ticks.Select(x => PowerReading.Average(bucketed[x], p => SensorValue(p, id))).ToArray())];
        else
        {
            var ids = _points.SelectMany(x => x.Total.SensorIds).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (ids.Length > 0 && _points.All(x => x.Total.Source is PowerSource.Components or PowerSource.None))
                Chart.Series = ids.Select((sensorId, i) => Series(
                    _choices.FirstOrDefault(x => x.Id == sensorId)?.Label ?? sensorId, i,
                    ticks.Select(x => Contribution(bucketed[x], sensorId)).ToArray())).ToArray();
            else
                Chart.Series = [Series(selected.Label, 0, averages)];
        }

        var hasHistory = selected.Id is { } selectedId
            ? _points.Any(x => x.Watts.ContainsKey(selectedId))
            : _points.Any(x => x.Total.Watts.HasValue);
        Chart.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        WaitMessage.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double? Contribution(IEnumerable<PowerReading> bucket, string id)
    {
        var included = false;
        var average = PowerReading.Average(bucket, reading =>
        {
            if (reading.Total.Watts is null) return null;
            if (!reading.Total.SensorIds.Contains(id) || !reading.Watts.TryGetValue(id, out var watts)) return 0;
            included = true;
            return watts;
        });
        return included ? average : null;
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
