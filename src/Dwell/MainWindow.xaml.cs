using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Dwell.Services;
using Dwell.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Drawing;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using WinRT;
using WinRT.Interop;

namespace Dwell;

public sealed partial class MainWindow : Window
{
    public OverviewViewModel Overview { get; }
    public SettingsViewModel Settings { get; }
    public ActivityViewModel Activity { get; }
    public RulesViewModel Rules { get; }
    private readonly TrackingService _tracker;
    private readonly UpdateService _updates;
    private readonly ReminderService _reminders;
    private readonly NativeMethods.SubclassProc _windowProc;
    private readonly nint _hwnd;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _visible;
    private bool _exiting;
    private bool _exitInProgress;
    private Views.UsageChart? _chart;
    private readonly UISettings _uiSettings = new();
    private MicaController? _mica;
    private SystemBackdropConfiguration? _backdrop;

    public MainWindow(OverviewViewModel overview, SettingsViewModel settings, ActivityViewModel activity, RulesViewModel rules, TrackingService tracker, UpdateService updates, ReminderService reminders)
    {
        Overview = overview; Settings = settings; Activity = activity; Rules = rules; _tracker = tracker; _updates = updates; _reminders = reminders;
        InitializeComponent();
        OverviewScroll.SizeChanged += (_, _) => VisualStateManager.GoToState(AdaptiveHost, OverviewScroll.ActualWidth >= 850 ? "Wide" : "Compact", false);
        Title = Strings.Get("AppTitle");
        _hwnd = WindowNative.GetWindowHandle(this);
        _windowProc = WindowProc;
        NativeMethods.SetWindowSubclass(_hwnd, _windowProc, 1, 0);
        NativeMethods.WTSRegisterSessionNotification(_hwnd, 0);
        AppWindow.Resize(new SizeInt32(1140, 720));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Dwell.ico"));
        Settings.ThemeChanged += ApplyTheme;
        Settings.LanguageChanged += ApplyLanguage;
        ApplyTheme(Settings.ThemeIndex);
        Root.ActualThemeChanged += (_, _) => { UpdateTitleBarTheme(); UpdatePersonalization(); };
        _uiSettings.ColorValuesChanged += PersonalizationChanged;
        _uiSettings.AdvancedEffectsEnabledChanged += PersonalizationChanged;
        Activated += (_, args) => { if (_backdrop is not null) _backdrop.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated; };
        Overview.ChartChanged += () => _chart?.Refresh();
        Overview.AppHistoryRequested += ShowAppHistory;
        HistoryPage.ExportRequested += ExportHistory;
        _tracker.PauseChanged += PauseChanged;
        _reminders.Reminder += NotifyReminder;
        ApplyLanguage();
        TrayIcon.Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "Dwell.ico"), 32, 32);
        TrayIcon.LeftClickCommand = new RelayCommand(Show);
        TrayIcon.ForceCreate(false);
        AppWindow.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; Hide(); } };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (!_visible) return;
            UpdatePauseState();
            if (IsEditing()) return;
            if (OverviewScroll.Visibility == Visibility.Visible) await Overview.RefreshAsync();
            else if (LimitsPage.Visibility == Visibility.Visible) await Rules.RefreshAsync();
        };
        _tracker.Start(NativeMethods.IsSessionLocked());
        _reminders.Start();
        var updateError = Path.Combine(SettingsService.DataDirectory, "update-error.txt");
        if (File.Exists(updateError))
        {
            UpdateStatus.Text = Strings.Get("UpdateFailedRollback");
            try { File.Delete(updateError); } catch (IOException) { }
        }
        Closed += (_, _) =>
        {
            _refreshTimer.Stop(); TrayIcon.Dispose();
            _reminders.Reminder -= NotifyReminder;
            _tracker.PauseChanged -= PauseChanged;
            _uiSettings.ColorValuesChanged -= PersonalizationChanged;
            _uiSettings.AdvancedEffectsEnabledChanged -= PersonalizationChanged;
            _mica?.Dispose();
            NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
            NativeMethods.RemoveWindowSubclass(_hwnd, _windowProc, 1);
            App.ShutdownServices();
        };
    }

    private void ApplyTheme(int index)
    {
        AdaptiveHost.RequestedTheme = index switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        UpdateTitleBarTheme();
        UpdatePersonalization();
    }

    private void PersonalizationChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(UpdatePersonalization);

    private void UpdatePersonalization()
    {
        var useMica = MicaController.IsSupported() && _uiSettings.AdvancedEffectsEnabled && !NativeMethods.HighContrastEnabled();
        FallbackSurface.Visibility = useMica ? Visibility.Collapsed : Visibility.Visible;
        if (!useMica) { _mica?.Dispose(); _mica = null; _backdrop = null; return; }
        _backdrop ??= new SystemBackdropConfiguration { IsInputActive = _visible };
        _backdrop.Theme = Root.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (_mica is null)
        {
            _mica = new MicaController();
            _mica.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            _mica.SetSystemBackdropConfiguration(_backdrop);
        }
        _chart?.Refresh();
    }

    private void ApplyLanguage()
    {

        AdaptiveHost.Language = Strings.Culture.Name;
        OverviewItem.Content = Strings.Get("OverviewNav.Content");
        SettingsItem.Content = Strings.Get("SettingsNav.Content");
        ActivityItem.Content = Strings.Get("ActivityTitle"); RulesItem.Content = Strings.Get("RulesTitle");
        SettingsHeading.Text = Strings.Get("SettingsTitle.Text");
        MostUsedHeading.Text = Strings.Get("MostUsedTitle.Text");
        EmptyMessage.Text = Strings.Get("EmptyState.Text");
        MoreButton.Content = Strings.Get("ShowMore.Content");
        DayTab.Text = Strings.Get("Day"); WeekTab.Text = Strings.Get("Week");
        ThemeCombo.Header = Strings.Get("ThemeCombo.Header");
        SystemThemeItem.Content = Strings.Get("SystemTheme.Content");
        LightThemeItem.Content = Strings.Get("LightTheme.Content");
        DarkThemeItem.Content = Strings.Get("DarkTheme.Content");
        LanguageCombo.Header = Strings.Get("LanguageCombo.Header");
        SystemLanguageItem.Content = Strings.Get("SystemLanguage.Content");
        IdleInput.Header = Strings.Get("IdleMinutes.Header");
        StartupSwitch.Header = Strings.Get("AutoStart.Header");
        StartupSettingsLink.Content = Strings.Get("StartupSettingsLink.Content");
        UpdateButton.Content = Strings.Get("InstallUpdate.Content");
        TrayOpen.Text = Strings.Get("TrayOpen.Text"); TrayExit.Text = Strings.Get("TrayExit.Text");
        HistoryPage.Localize(); LimitsPage.Localize(); UpdatePauseState();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PreviousButton, Strings.Get("PreviousPeriodLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextButton, Strings.Get("NextPeriodLabel"));
        if (_visible) { _ = Overview.RefreshAsync(); _ = Activity.RefreshAsync(); _ = Rules.RefreshAsync(); }
    }

    private void UpdateTitleBarTheme()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    public async void Show()
    {
        if (_exiting) return;
        if (Strings.FollowSystemLanguage()) ApplyLanguage();
        _visible = true;
        if (OverviewScroll.Visibility == Visibility.Visible) EnsureChart();
        _refreshTimer.Start();
        NativeMethods.ShowWindow(_hwnd, 9);
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        if (OverviewScroll.Visibility == Visibility.Visible) await Overview.RefreshAsync();
        else if (HistoryPage.Visibility == Visibility.Visible) await Activity.RefreshAsync();
        else if (LimitsPage.Visibility == Visibility.Visible) await Rules.RefreshAsync();
    }

    private void EnsureChart()
    {
        if (_chart is not null) return;
        _chart = new Views.UsageChart { ViewModel = Overview };
        ChartHost.Content = _chart;
    }

    public void Hide()
    {
        _visible = false;
        _refreshTimer.Stop();
        AppWindow.Hide();
        ChartHost.Content = null;
        _chart = null;
    }

    private async void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (OverviewScroll is null || SettingsScroll is null) return;
        var page = args.SelectedItemContainer?.Tag as string ?? "overview";
        OverviewScroll.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        LimitsPage.Visibility = page == "rules" ? Visibility.Visible : Visibility.Collapsed;
        if (!_visible) return;
        if (page == "overview") { EnsureChart(); await Overview.RefreshAsync(); }
        else if (page == "activity") await Activity.RefreshAsync();
        else if (page == "rules") await Rules.RefreshAsync();
    }

    private bool IsEditing()
    {
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is UIElement; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or ComboBox or TimePicker or NumberBox) return true;
        return false;
    }

    private void UpdatePauseState()
    {
        TrayPause.Text = Strings.Get(_tracker.IsPaused ? "ResumeTracking" : "PauseTracking");
        TrackingStatusText.Text = _tracker.IsPaused ? Strings.Get("TrackingPaused") : "";
        TrayIcon.ToolTipText = _tracker.IsPaused ? "Dwell: " + Strings.Get("TrackingPaused") : "Dwell";
    }
    private void PauseChanged() => DispatcherQueue.TryEnqueue(() => { UpdatePauseState(); if (_visible) _ = Overview.RefreshAsync(); });

    private void OpenStartupSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            Settings.Error = "";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Settings.Error = Strings.Get("StartupSettingsError");
        }
    }
    private void PauseClicked(object sender, RoutedEventArgs e) => _tracker.TogglePause();
    private void ShowAppHistory(string name, DateTime date, bool week) { Activity.ShowApp(name, date, week); Navigation.SelectedItem = ActivityItem; }

    private void NotifyReminder(UsageReminder reminder) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_exiting) return;
        var message = reminder.Kind switch
        {
            ReminderKind.Limit when reminder.Limit is { } limit => Strings.Format("LimitReached", UsageProjection.TargetName(limit.Target, limit.Name), limit.Minutes),
            ReminderKind.Downtime => Strings.Get("DowntimeReminder"),
            _ => Strings.Format("BreakReminder", reminder.Minutes)
        };
        TrayIcon.ShowNotification("Dwell", message, H.NotifyIcon.Core.NotificationIcon.Info, sound: false, respectQuietTime: true);
    });

    private async void ExportHistory()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "Dwell-" + (Activity.Date ?? DateTimeOffset.Now).ToString("yyyy-MM-dd") };
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await Activity.ExportAsync(file.Path);
            HistoryPage.SetExportStatus(Strings.Get("ExportSaved"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { HistoryPage.SetExportStatus(Strings.Get("ExportError")); }
    }

    private void PeriodChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Overview is not null) Overview.PeriodIndex = sender.SelectedItem == WeekTab ? 0 : 1;
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x001A) DispatcherQueue.TryEnqueue(() => { if (Strings.FollowSystemLanguage()) ApplyLanguage(); UpdatePersonalization(); });
        else if (message == 0x02B1)
        {
            if (wParam is 7 or 4 or 2 or 6) _tracker.SetLocked(true);
            else if (wParam is 8 or 3 or 1) _tracker.SetLocked(NativeMethods.IsSessionLocked());
        }
        else if (message == 0x0218)
        {
            if (wParam == 4) _tracker.SetSleeping(true);
            else if (wParam is 7 or 18) { _tracker.SetLocked(NativeMethods.IsSessionLocked()); _tracker.SetSleeping(false); }
        }
        else if (message == 0x0011) { _tracker.SetLocked(true); return 1; } // end session: checkpoint synchronously
        else if (message == 0x0016)
        {
            if (wParam != 0) { _exiting = true; TrayIcon.Dispose(); }
            else _tracker.SetLocked(NativeMethods.IsSessionLocked());
        }
        else if (message == 0x0005 && wParam == 1) DispatcherQueue.TryEnqueue(Hide);
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void TrayOpenClicked(object sender, RoutedEventArgs e) => Show();
    private async void TrayExitClicked(object sender, RoutedEventArgs e) => await ExitAsync();

    private async Task<bool> ExitAsync(bool forUpdate = false)
    {
        if (_exitInProgress) return false;
        _exitInProgress = true;
        if (!await _tracker.SaveAsync())
        {
            _exitInProgress = false;
            Show();
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = Strings.Get("ExitErrorTitle"), Content = Strings.Get("StorageError"), CloseButtonText = Strings.Get("Close") }.ShowAsync();
            return false;
        }
        if (forUpdate) return true;
        await _tracker.StopAsync();
        _exiting = true; Close(); return true;
    }

    private async void InstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".zip");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var prepared = await _updates.PrepareAsync(file.Path);
            var confirm = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Strings.Get("UpdateDialogTitle"),
                Content = Strings.Format("UpdateConfirmation", prepared.Version),
                PrimaryButtonText = Strings.Get("UpdateRestart"), CloseButtonText = Strings.Get("Cancel"), DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) { _updates.Discard(prepared); return; }
            if (!await ExitAsync(true)) { _updates.Discard(prepared); return; }
            _updates.Launch(prepared);
            await _tracker.StopAsync();
            _exiting = true; Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            _exitInProgress = false;
            UpdateStatus.Text = Strings.Get("UpdateError");
        }
        finally { UpdateButton.IsEnabled = true; }
    }
}
