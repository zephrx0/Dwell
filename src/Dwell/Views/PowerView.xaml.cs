using Dwell.Core;
using Dwell.Services;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SkiaSharp;

namespace Dwell.Views;

public sealed record PowerSensorRow(string Device, string Sensor, string Wattage, string Included);

public sealed partial class PowerView : UserControl
{
    private const long WindowMilliseconds = 300_000;
    private readonly HardwarePowerMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Queue<(long At, double? Watts)> _points = new();
    private readonly LineSeries<ObservablePoint> _line = new()
    {
        Fill = null, LineSmoothness = 0, GeometrySize = 4, EnableNullSplitting = true,
        XToolTipLabelFormatter = point => FormatTime(point.Coordinate.SecondaryValue),
        YToolTipLabelFormatter = point => FormatWatts(point.Coordinate.PrimaryValue)
    };
    private readonly Axis _timeAxis = new() { MinLimit = -30, MaxLimit = 0, MinStep = 15, ForceStepToMin = true };
    private readonly Axis _wattsAxis = new() { MinLimit = 0, MinStep = 1 };
    private HardwarePowerSample? _sample;
    private PowerTotal _lastSource = PowerTotal.Unavailable;
    private bool _reading;

    public PowerView()
    {
        InitializeComponent();
        Chart.Series = [_line];
        Chart.XAxes = [_timeAxis];
        Chart.YAxes = [_wattsAxis];
        _timeAxis.Labeler = FormatTime;
        _wattsAxis.Labeler = value => value.ToString("0.#", Strings.Culture) + " W";
        _timer.Tick += async (_, _) => await ReadAsync();
        Loaded += (_, _) => ApplyChartTheme();
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyChartTheme);
        Localize();
    }

    internal void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = ReadAsync();
    }

    internal void Stop()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        if (_points.Count > 0) _points.Enqueue((Environment.TickCount64, null));
    }

    internal void Close()
    {
        Stop();
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
            if (sample.Total.Watts is not null)
            {
                if (sample.Total.Source != _lastSource.Source || !sample.Total.SensorIds.SequenceEqual(_lastSource.SensorIds))
                    _points.Clear();
                _lastSource = sample.Total;
            }

            var now = Environment.TickCount64;
            _points.Enqueue((now, sample.Total.Watts));
            while (_points.Peek().At < now - WindowMilliseconds) _points.Dequeue();
            var duration = Math.Max(30, Math.Ceiling((now - _points.Peek().At) / 30_000d) * 30);
            _timeAxis.MinLimit = -duration;
            _timeAxis.MinStep = duration <= 60 ? 15 : 60;
            _line.Values = _points.Select(x => new ObservablePoint((x.At - now) / 1000d, x.Watts)).ToArray();
            RenderSample();
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
        WindowLabel.Text = Strings.Get("PowerWindow");
        RenderSample();
    }

    private void RenderSample()
    {
        var total = _sample?.Total ?? PowerTotal.Unavailable;
        Total.Text = FormatWatts(total.Watts);
        Scope.Text = Strings.Get("PowerSource" + total.Source);
        WaitMessage.Text = Strings.Get(_sample is null ? "PowerReading" : "PowerNoReadings");
        var hasHistory = _points.Any(x => x.Watts is not null);
        Chart.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        WaitMessage.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        var included = total.SensorIds.ToHashSet(StringComparer.Ordinal);
        var yes = Strings.Get("PowerYes");
        var rows = new List<PowerSensorRow>();
        if (_sample?.BatteryWatts is { } battery)
            rows.Add(new(Strings.Get("PowerBattery"), Strings.Get("PowerDischarge"), FormatWatts(battery), total.Source == PowerSource.Battery ? yes : ""));
        foreach (var device in _sample?.Devices ?? [])
        {
            if (device.Sensors.Length == 0)
                rows.Add(new(device.Name, Strings.Get("PowerNoSensor"), FormatWatts(null), ""));
            foreach (var sensor in device.Sensors)
                rows.Add(new(device.Name, sensor.Name, FormatWatts(sensor.Watts), included.Contains(sensor.Id) ? yes : ""));
        }
        Sensors.ItemsSource = rows;
    }

    private void ApplyChartTheme()
    {
        var text = Paint(AxisSwatch);
        _timeAxis.LabelsPaint = text;
        _wattsAxis.LabelsPaint = text;
        _wattsAxis.SeparatorsPaint = Paint(GridSwatch);
        _line.Stroke = Paint(AccentSwatch, 2);
        _line.GeometryFill = _line.Stroke;
        Chart.TooltipBackgroundPaint = Paint(TooltipBackgroundSwatch);
        Chart.TooltipTextPaint = Paint(TooltipTextSwatch);
    }

    private static SolidColorPaint Paint(Rectangle swatch, float strokeWidth = 1)
    {
        var brush = (SolidColorBrush)swatch.Fill;
        var color = brush.Color;
        return new SolidColorPaint(new SKColor(color.R, color.G, color.B, (byte)Math.Round(color.A * brush.Opacity))) { StrokeThickness = strokeWidth };
    }

    private static string FormatWatts(double? watts) =>
        watts is { } value ? value.ToString("N1", Strings.Culture) + " W" : "\u2014";

    private static string FormatTime(double seconds) =>
        seconds >= 0 ? Strings.Get("PowerNow") : "-" + TimeSpan.FromSeconds(-seconds).ToString(@"m\:ss");
}
