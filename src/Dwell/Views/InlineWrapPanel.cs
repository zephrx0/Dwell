using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Dwell.Views;

public sealed class InlineWrapPanel : Panel
{
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, usedWidth = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed) continue;
            if (!arrange) child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            var left = x > 0 ? x + ColumnSpacing : 0;
            if (x > 0 && left + size.Width > width)
            {
                y += lineHeight + RowSpacing;
                left = 0;
                lineHeight = 0;
            }
            if (arrange) child.Arrange(new Rect(left, y, size.Width, size.Height));
            x = left + size.Width;
            usedWidth = Math.Max(usedWidth, x);
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(usedWidth, y + lineHeight);
    }
}
