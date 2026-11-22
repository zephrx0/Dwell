using Dwell.Services;
using Dwell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dwell.Views;

public sealed partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();
        Localize();
    }
    public void Localize()
    {
        PageTitle.Text = Strings.Get("RulesTitle");
        Heading.Text = Strings.Get("DailyLimits");
        TargetCombo.Header = Strings.Get("LimitTarget"); NewLimit.Header = Strings.Get("LimitMinutes");
        ToolTipService.SetToolTip(AddButton, Strings.Get("AddLimit"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AddButton, Strings.Get("AddLimit"));
        RestToggle.Header = Strings.Get("Downtime"); StartTime.Header = Strings.Get("StartTime"); EndTime.Header = Strings.Get("EndTime");
        AllowedHeading.Text = Strings.Get("AlwaysAllowed");
        AllowedCombo.PlaceholderText = Strings.Get("ChooseApp"); ToolTipService.SetToolTip(AllowButton, Strings.Get("AddException"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AllowButton, Strings.Get("AddException"));
        BreakToggle.Header = Strings.Get("BreakReminders"); BreakInterval.Header = Strings.Get("BreakInterval");
    }
    private async void RemoveAllowedClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AllowedApp app } && DataContext is RulesViewModel vm) await vm.RemoveAllowedCommand.ExecuteAsync(app);
    }
}
