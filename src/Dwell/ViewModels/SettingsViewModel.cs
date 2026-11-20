using CommunityToolkit.Mvvm.ComponentModel;
using Dwell.Services;

namespace Dwell.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private bool _initializing = true;
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial double IdleMinutes { get; set; }
    [ObservableProperty] public partial int LanguageIndex { get; set; }
    [ObservableProperty] public partial bool AutoStart { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    public event Action<int>? ThemeChanged;
    public event Action? LanguageChanged;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        ThemeIndex = settings.Current.Theme;
        IdleMinutes = settings.Current.IdleMinutes;
        LanguageIndex = settings.Current.Language;
        AutoStart = settings.AutoStart;
        _initializing = false;
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_initializing || value is < 0 or > 2) return;
        _settings.Current.Theme = value;
        Save(); ThemeChanged?.Invoke(value);
    }

    partial void OnIdleMinutesChanged(double value)
    {
        if (_initializing || !double.IsFinite(value)) return;
        _settings.Current.IdleMinutes = (int)Math.Clamp(value, 1, 60);
        Save();
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_initializing || value is < 0 or > 2) return;
        _settings.Current.Language = value;
        Save();
        Strings.SetLanguage(value);
        LanguageChanged?.Invoke();
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_initializing) return;
        try { _settings.AutoStart = value; Error = ""; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { Error = Strings.Get("SettingsError"); }
    }

    private void Save()
    {
        try { _settings.Save(); Error = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = Strings.Get("SettingsError"); }
    }
}
