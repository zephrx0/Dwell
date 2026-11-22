using System.ComponentModel;
using System.Diagnostics;
using Dwell.Services;
using Dwell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dwell.Views;

public sealed partial class RulesView : UserControl
{
    private readonly DispatcherTimer _shutdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _shutdownAt;
    private bool _requestingShutdown;
    private string _shutdownMessage = "ShutdownHint";

    public RulesView()
    {
        InitializeComponent();
        _shutdownTimer.Tick += ShutdownTick;
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
        ShutdownHeading.Text = Strings.Get("ShutdownHeading");
        ShutdownMinutesLabel.Text = Strings.Get("ShutdownMinutes");
        RenderShutdownTimer();
    }

    private void ShutdownClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdownTimer.IsEnabled)
        {
            _shutdownTimer.Stop();
            _shutdownMessage = "ShutdownCanceled";
        }
        else if (ShutdownMinutes.Value is >= 1 and <= 1440 and var minutes && minutes == Math.Truncate(minutes))
        {
            _shutdownAt = Environment.TickCount64 + (long)minutes * 60_000;
            _shutdownTimer.Start();
        }
        else
        {
            _shutdownMessage = "ShutdownInvalidMinutes";
        }
        RenderShutdownTimer();
    }

    private async void ShutdownTick(object? sender, object e)
    {
        if (!_shutdownTimer.IsEnabled) return;
        if (Environment.TickCount64 < _shutdownAt)
        {
            RenderShutdownTimer();
            return;
        }

        _shutdownTimer.Stop();
        _requestingShutdown = true;
        _shutdownMessage = "ShutdownRequesting";
        RenderShutdownTimer();
        try
        {
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/t");
            start.ArgumentList.Add("0");
            using var process = Process.Start(start);
            if (process is not null) await process.WaitForExitAsync();
            _shutdownMessage = process?.ExitCode == 0 ? "ShutdownRequested" : "ShutdownFailed";
        }
        catch (Win32Exception ex)
        {
            Trace.WriteLine(ex);
            _shutdownMessage = "ShutdownFailed";
        }
        finally
        {
            _requestingShutdown = false;
            RenderShutdownTimer();
        }
    }

    internal void StopShutdownTimer() => _shutdownTimer.Stop();

    private void RenderShutdownTimer()
    {
        var running = _shutdownTimer.IsEnabled;
        ShutdownMinutes.IsEnabled = !running && !_requestingShutdown;
        ShutdownButton.IsEnabled = !_requestingShutdown;
        ShutdownButton.Content = Strings.Get(running ? "ShutdownCancel" : "ShutdownStart");
        var seconds = Math.Max(0, (_shutdownAt - Environment.TickCount64 + 999) / 1000);
        ShutdownStatus.Text = running
            ? Strings.Format("ShutdownRemaining", seconds / 3600, seconds / 60 % 60, seconds % 60)
            : Strings.Get(_shutdownMessage);
    }
    private async void RemoveAllowedClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AllowedApp app } && DataContext is RulesViewModel vm) await vm.RemoveAllowedCommand.ExecuteAsync(app);
    }
}
