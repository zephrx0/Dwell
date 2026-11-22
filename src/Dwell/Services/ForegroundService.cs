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
    private long _lastGamepadInput = long.MinValue;
    private readonly uint?[] _gamepadPackets = new uint?[4];

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
        return window == _lastWindow && pid == _lastPid && _lastApp is not null ? _lastApp.Exe : ApplicationPath(window, pid);
    }

    public TimeSpan IdleTime()
    {
        var now = Environment.TickCount64;
        for (uint index = 0; index < _gamepadPackets.Length; index++)
        {
            if (NativeMethods.XInputGetState(index, out var state) != 0)
            {
                _gamepadPackets[index] = null;
                continue;
            }
            if (_gamepadPackets[index] is { } previous && previous != state.Packet)
                _lastGamepadInput = now;
            _gamepadPackets[index] = state.Packet;
        }
        var input = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        var idle = NativeMethods.GetLastInputInfo(ref input)
            ? IdlePolicy.Elapsed(unchecked((uint)now), input.Time)
            : TimeSpan.MaxValue;
        return _lastGamepadInput == long.MinValue ? idle :
            TimeSpan.FromMilliseconds(Math.Min(idle.TotalMilliseconds, now - _lastGamepadInput));
    }

    private static string? ApplicationPath(nint window, uint pid)
    {
        if (window == 0 || pid == 0) return null;
        var path = ProcessPath(pid);
        if (!string.Equals(Path.GetFileName(path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return path;
        string? childPath = null;
        var className = new StringBuilder(64);
        NativeMethods.EnumChildWindows(window, (child, _) =>
        {
            NativeMethods.GetClassName(child, className, className.Capacity);
            if (!string.Equals(className.ToString(), "Windows.UI.Core.CoreWindow", StringComparison.Ordinal)) return true;
            NativeMethods.GetWindowThreadProcessId(child, out var childPid);
            if (childPid == pid || childPid == 0) return true;
            childPath = ProcessPath(childPid);
            return childPath is null;
        }, 0);
        return childPath;
    }

    public async Task<AppRecord?> GetAppAsync()
    {
        var window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (window == 0 || pid == 0) return null;
        if (window == _lastWindow && pid == _lastPid && Environment.TickCount64 - _lastLookup < (_lastApp is null ? 1000 : 30000)) return _lastApp;
        _lastWindow = window; _lastPid = pid; _lastLookup = Environment.TickCount64;
        _lastApp = null;
        var path = ApplicationPath(window, pid);
        if (path is null || !ApplicationPolicy.IsUserApplication(path)) return null;
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
