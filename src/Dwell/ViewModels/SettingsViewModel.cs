using CommunityToolkit.Mvvm.ComponentModel;
using Dwell.Core;
using Dwell.Services;

namespace Dwell.ViewModels;

public sealed record ExcludedAppItem(string Key, string Name)
{
    public string RestoreLabel => Strings.Get("RestoreToStatistics");
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AppRepository _repository;
    private bool _initializing = true;
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial double IdleMinutes { get; set; }
    [ObservableProperty] public partial int LanguageIndex { get; set; }
    [ObservableProperty] public partial bool AutoStart { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial ExcludedAppItem[] ExcludedApps { get; set; } = [];
    public event Action<int>? ThemeChanged;
    public event Action? LanguageChanged;

    public SettingsViewModel(SettingsService settings, AppRepository repository)
    {
        _settings = settings;
        _repository = repository;
        ThemeIndex = settings.Current.Theme;
        IdleMinutes = settings.Current.IdleMinutes;
        LanguageIndex = settings.Current.Language;
        AutoStart = settings.AutoStart;
        _initializing = false;
    }

    public void RefreshExcludedApps() => ExcludedApps = UsageProjection.Groups(_repository.KnownApps(), includeIgnored: true)
        .Where(group => group.Variants.Any(app => app.Ignored))
        .Select(group => new ExcludedAppItem(group.Key, ApplicationPolicy.DisplayName(group.App.Name)))
        .OrderBy(app => app.Name, StringComparer.Create(Strings.Culture, ignoreCase: true)).ToArray();

    public void RestoreApp(ExcludedAppItem app)
    {
        try
        {
            _repository.SetIgnored(app.Key, false);
            RefreshExcludedApps();
            Error = "";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { Error = Strings.Get("StorageError"); }
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
