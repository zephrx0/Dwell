using Dwell.Services;
using Dwell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Dwell;

public partial class App : Application
{
    private static ServiceProvider? _services;
    private readonly SingleInstance _instance = new();
    private MainWindow? _window;
    private readonly SettingsService _settings;
    public App()
    {
        _settings = new SettingsService();
        Strings.SetLanguage(_settings.Current.Language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_instance.IsFirst) { _instance.Dispose(); Exit(); return; }
        _services = new ServiceCollection()
            .AddSingleton(_settings).AddSingleton<AppRepository>()
            .AddSingleton<BackgroundTracker>().AddSingleton<ForegroundService>().AddSingleton<TrackingService>()
            .AddSingleton<OverviewViewModel>().AddSingleton<SettingsViewModel>()
            .AddSingleton<ExcludedAppsViewModel>()
            .AddSingleton<ActivityViewModel>().AddSingleton<RulesViewModel>().AddSingleton<ReminderService>()
            .AddSingleton<UpdateService>().AddSingleton<MainWindow>().BuildServiceProvider();
        _window = _services.GetRequiredService<MainWindow>();
        try { _settings.SynchronizeAutoStart(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        _instance.Listen(() => _window.DispatcherQueue.TryEnqueue(_window.Show));
        _window.Closed += (_, _) => _instance.Dispose();
        if (Environment.GetCommandLineArgs().Contains("--background")) _window.Hide();
        else _window.Show();
    }

    internal static void ShutdownServices() { _services?.Dispose(); _services = null; }
}
