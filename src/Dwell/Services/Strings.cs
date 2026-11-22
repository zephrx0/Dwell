using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Dwell.Services;

public static class Strings
{
    private static ResourceLoader? _loader;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public static int LanguageOption { get; private set; }
    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();

    public static void SetLanguage(int option)
    {
        LanguageOption = option;
        var language = option switch { 1 => "ru-RU", 2 => "en-US", _ => SystemLanguage() };
        Culture = CultureInfo.GetCultureInfo(language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US");
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Culture.Name;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        _loader = null;
    }

    public static bool FollowSystemLanguage()
    {
        if (LanguageOption != 0) return false;
        var system = SystemLanguage().StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US";
        if (system == Culture.Name) return false;
        SetLanguage(0);
        return true;
    }

    private static string SystemLanguage()
    {
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).Name; }
        catch (CultureNotFoundException) { return CultureInfo.InstalledUICulture.Name; }
    }

    public static string Get(string key) => (_loader ??= new ResourceLoader()).GetString(key.Replace('.', '/'));
    public static string Format(string key, params object[] values) => string.Format(Culture, Get(key), values);
    public static string Duration(double seconds)
    {
        if (seconds is > 0 and < 60) return Get("LessThanMinute");
        var minutes = (int)Math.Max(0, seconds / 60);
        return minutes >= 60 ? Format("HoursMinutes", minutes / 60, minutes % 60) : Format("Minutes", minutes);
    }
}
