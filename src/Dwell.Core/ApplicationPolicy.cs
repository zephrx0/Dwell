namespace Dwell.Core;

public static class ApplicationPolicy
{
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
    private static readonly HashSet<string> WindowsExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "PickerHost.exe",
        "FilePicker.exe", "ApplicationFrameHost.exe", "RuntimeBroker.exe", "SearchHost.exe", "SearchApp.exe",
        "LockApp.exe", "SystemSettings.exe", "SnippingTool.exe", "ScreenClippingHost.exe", "notepad.exe",
        "mspaint.exe", "CalculatorApp.exe", "calc.exe", "Photos.exe", "Microsoft.Photos.exe", "Camera.exe"
    };
    private static readonly string[] WindowsPackages =
    [
        "Microsoft.ScreenSketch_", "Microsoft.WindowsNotepad_", "Microsoft.Paint_", "Microsoft.WindowsCalculator_",
        "Microsoft.Windows.Photos_", "Microsoft.WindowsCamera_", "Microsoft.ZuneMusic_", "Microsoft.ZuneVideo_",
        "Microsoft.YourPhone_", "Microsoft.WindowsTerminal_", "MicrosoftWindows.Client.",
        "microsoft.windowscommunicationsapps_", "Microsoft.WindowsMaps_", "Microsoft.WindowsAlarms_",
        "Microsoft.WindowsSoundRecorder_", "Microsoft.GetHelp_", "Microsoft.Getstarted_",
        "Microsoft.WindowsFeedbackHub_", "Microsoft.BingWeather_", "Microsoft.BingNews_", "Microsoft.People_",
        "Microsoft.WindowsStore_", "Microsoft.StorePurchaseApp_", "Microsoft.DesktopAppInstaller_",
        "Microsoft.SecHealthUI_", "MicrosoftCorporationII.QuickAssist_"
    ];

    public static bool IsUserApplication(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var path = exe.Replace('/', '\\');
        if (WindowsDirectory.Length > 0 && path.StartsWith(WindowsDirectory + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (WindowsExecutables.Contains(Path.GetFileName(path))) return false;
        return !WindowsPackages.Any(package => path.Contains("\\" + package, StringComparison.OrdinalIgnoreCase));
    }

    public static string Identity(string exe, string name) =>
        (Path.GetFileName(exe).ToUpperInvariant() is "JAVA.EXE" or "JAVAW.EXE" ? exe.ToUpperInvariant() : Path.GetFileName(exe).ToUpperInvariant())
        + "|" + DisplayName(name).ToUpperInvariant();

    public static string DisplayName(string name)
    {
        var displayName = name.Trim();
        if (displayName.Equals("Dwell Portable", StringComparison.OrdinalIgnoreCase)) return "Dwell";
        return displayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? displayName[..^4] : displayName;
    }
}
