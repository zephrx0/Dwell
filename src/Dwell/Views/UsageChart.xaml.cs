using Dwell.Services;
using Dwell.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.VisualElements;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SkiaSharp;
using Windows.UI.ViewManagement;

namespace Dwell.Views;

public sealed partial class UsageChart : UserControl
{
    public OverviewViewModel? ViewModel { get; set; }
    private readonly UISettings _uiSettings = new();
    public UsageChart()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        SizeChanged += (_, _) =>
        {
            var step = ActualWidth < 380 ? 6 : ActualWidth < 660 ? 4 : 2;
            if (_dayStep == step) return;
            _dayStep = step;
            Refresh();
        };
        Loaded += (_, _) => { _uiSettings.ColorValuesChanged += ColorsChanged; DispatcherQueue.TryEnqueue(Refresh); };
        Unloaded += (_, _) => _uiSettings.ColorValuesChanged -= ColorsChanged;
    }

    private int _dayStep = 4;

    private void ColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);

    private static SKColor ColorOf(Rectangle swatch)
    {
        var color = ((SolidColorBrush)swatch.Fill).Color;
        return new(color.R, color.G, color.B, (byte)Math.Round(color.A * swatch.Fill.Opacity));
    }

    public void Refresh()
    {
        if (ViewModel is not { } vm || !IsLoaded) return;
        var count = vm.Buckets[0].Length;
        var week = count == 7;
        var days = week ? Strings.Get("Weekdays").Split('|') : [];
        var text = ColorOf(TextSwatch);
        var grid = ColorOf(GridSwatch);
        Chart.TooltipBackgroundPaint = new SolidColorPaint(ColorOf(TooltipBackgroundSwatch));
        Chart.TooltipTextPaint = new SolidColorPaint(ColorOf(TooltipTextSwatch));
        Rectangle[] swatches = [Swatch0, Swatch1, Swatch2, Swatch3, Swatch4];
        Chart.Series = vm.Buckets.Select((values, i) => (ISeries)new StackedColumnSeries<double?>
        {
            Values = values.Select(x => x > 0 ? (double?)(x / 3600) : null).ToArray(),
            Name = Strings.Get("Category" + (Dwell.Core.AppCategory)i),
            XToolTipLabelFormatter = point =>
            {
                var index = (int)point.Coordinate.SecondaryValue;
                return week ? days.ElementAtOrDefault(index) ?? "" : $"{index:00}:00";
            },
            YToolTipLabelFormatter = point => $"{point.Context.Series.Name}: {Strings.Duration(point.Coordinate.PrimaryValue * 3600)}",
            Fill = new SolidColorPaint(ColorOf(swatches[i])), Stroke = null,
            MaxBarWidth = week ? 36 : 20, Padding = 3,
            Rx = 0, Ry = 0, StackGroup = 0
        }).ToArray();
        Chart.XAxes = [new Axis
        {
            Labeler = week ? value => days.ElementAtOrDefault((int)value) ?? "" : value => value.ToString("00"),
            MinLimit = -0.5, MaxLimit = week ? 6.5 : 24,
            MinStep = week ? 1 : _dayStep, ForceStepToMin = true,
            TextSize = 12, LabelsPaint = new SolidColorPaint(text), SeparatorsPaint = null, TicksPaint = null
        }];
        var peak = Enumerable.Range(0, count).Max(i => vm.Buckets.Sum(x => x[i])) / 3600;
        Chart.YAxes = [new Axis
        {
            MinLimit = 0, MaxLimit = Math.Max(5d / 60, Math.Ceiling(peak * 60 / 5) * 5 / 60), MinStep = 1d / 60,
            Labeler = value => value < 1 ? Strings.Format("Minutes", Math.Round(value * 60)) : Strings.Format("Hours", Math.Round(value, 1)),
            LabelsPaint = new SolidColorPaint(text), TextSize = 12,
            SeparatorsPaint = new SolidColorPaint(grid) { StrokeThickness = 1 }, TicksPaint = null
        }];
        Chart.Sections = week ? [new RectangularSection
        {
            Yi = vm.AverageSeconds / 3600, Yj = vm.AverageSeconds / 3600,
            Stroke = new SolidColorPaint(text) { StrokeThickness = 1, PathEffect = new DashEffect([4, 4]) }
        }] : [];
    }
}
