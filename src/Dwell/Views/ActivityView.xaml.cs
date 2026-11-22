using Dwell.Services;
using Dwell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dwell.Views;

public sealed partial class ActivityView : UserControl
{
    public event Action? ExportRequested;
    public ActivityView()
    {
        InitializeComponent();
        RefreshDateRange();
        Localize();
    }
    internal void RefreshDateRange()
    {
        var today = DateTime.Today;
        if (DatePicker.MaxDate.LocalDateTime.Date != today)
            DatePicker.MaxDate = new DateTimeOffset(today);
    }

    public void Localize()
    {
        Heading.Text = Strings.Get("ActivityTitle"); ExportButton.Content = Strings.Get("ExportCsv");
        Search.PlaceholderText = Strings.Get("SearchApps");
        WeekItem.Content = Strings.Get("Week"); DayItem.Content = Strings.Get("Day");
        EmptyLabel.Text = Strings.Get("NoSessions");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RefreshButton, Strings.Get("Refresh"));
        ToolTipService.SetToolTip(RefreshButton, Strings.Get("Refresh"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Search, Strings.Get("SearchApps"));
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) { if (DataContext is ActivityViewModel vm) await vm.RefreshAsync(); }
    private void ExportClicked(object sender, RoutedEventArgs e) => ExportRequested?.Invoke();
    public void SetExportStatus(string text) => ExportStatus.Text = text;
}
