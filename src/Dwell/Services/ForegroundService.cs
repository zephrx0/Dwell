using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dwell.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Dwell.Services;

public sealed class ForegroundService(AppRepository repository)
{
    private nint _lastWindow;
    private uint _lastPid;
    private AppRecord? _lastApp;
    private long _lastLookup;

    internal static string? ProcessPath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(32768); uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    internal string? CurrentPath()
    {
        var window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        return window == _lastWindow && pid == _lastPid && _lastApp is not null ? _lastApp.Exe : ProcessPath(pid);
    }

    public static TimeSpan IdleTime()
    {
        var input = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        return NativeMethods.GetLastInputInfo(ref input)
            ? IdlePolicy.Elapsed(unchecked((uint)Environment.TickCount64), input.Time)
            : TimeSpan.MaxValue;
    }

    public async Task<AppRecord?> GetAppAsync()
    {
        var window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (window == 0 || pid == 0) return null;
        if (window == _lastWindow && pid == _lastPid && Environment.TickCount64 - _lastLookup < 30000) return _lastApp;
        _lastWindow = window; _lastPid = pid; _lastLookup = Environment.TickCount64;
        _lastApp = null;
        var process = NativeMethods.OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        string path;
        try
        {
            var buffer = new StringBuilder(32768);
            uint length = (uint)buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length)) return null;
            path = buffer.ToString();
        }
        finally { NativeMethods.CloseHandle(process); }
        if (!ApplicationPolicy.IsUserApplication(path)) return null;
        if (repository.Find(path) is { } known) return _lastApp = known;
        var name = Path.GetFileNameWithoutExtension(path);
        try { name = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description ? description : name; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        var icon = await ExtractIconAsync(path);
        return _lastApp = repository.Add(path, name, icon);
    }

    private static async Task<string> ExtractIconAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 48, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null) return "";
            var directory = Path.Combine(SettingsService.DataDirectory, "icons");
            Directory.CreateDirectory(directory);
            var icon = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))) + ".png");
            using var input = thumbnail.AsStreamForRead();
            using var output = File.Create(icon);
            await input.CopyToAsync(output);
            return icon;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException) { return ""; }
    }
}
