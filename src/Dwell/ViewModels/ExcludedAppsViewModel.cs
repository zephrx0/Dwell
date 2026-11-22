using CommunityToolkit.Mvvm.ComponentModel;
using Dwell.Core;
using Dwell.Services;

namespace Dwell.ViewModels;

public sealed record ExcludedAppItem(string Key, string Name, string Executable)
{
    public string RestoreLabel => Strings.Get("RestoreToStatistics");
}

public sealed partial class ExcludedAppsViewModel(AppRepository repository) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApps))]
    public partial ExcludedAppItem[] Apps { get; private set; } = [];

    [ObservableProperty] public partial string Error { get; private set; } = "";
    public bool HasApps => Apps.Length > 0;

    public void Refresh() => Apps = UsageProjection.Groups(repository.KnownApps(), includeIgnored: true)
        .Where(group => group.Variants.Any(app => app.Ignored))
        .Select(group => new ExcludedAppItem(group.Key, ApplicationPolicy.DisplayName(group.App.Name), Path.GetFileName(ApplicationPolicy.ExecutablePath(group.App.Exe))))
        .OrderBy(app => app.Name, StringComparer.Create(Strings.Culture, ignoreCase: true)).ToArray();

    public void Restore(ExcludedAppItem app)
    {
        try
        {
            repository.SetIgnored(app.Key, false);
            Refresh();
            Error = "";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Error = Strings.Get("StorageError");
        }
    }
}
