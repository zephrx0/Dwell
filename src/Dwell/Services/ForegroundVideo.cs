using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media;
using Windows.Media.Control;

namespace Dwell.Services;

internal sealed class ForegroundVideo
{
    private Task<GlobalSystemMediaTransportControlsSessionManager>? _manager;
    private long _retryAt;
    private nint _window;
    private uint _pid;
    private string? _appId;

    public async Task<bool> IsPlayingAsync(nint window, uint pid, string path, bool fullScreen, CancellationToken cancellationToken)
    {
        if (Environment.TickCount64 < _retryAt) return false;
        try
        {
            if (_window != window || _pid != pid)
            {
                _appId = GetWindowAppId(window) ?? GetProcessAppId(pid);
                _window = window;
                _pid = pid;
            }

            _manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            var manager = await _manager.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            foreach (var session in manager.GetSessions())
            {
                var id = session.SourceAppUserModelId;
                if (string.IsNullOrEmpty(id)) continue;
                if (!string.Equals(id, _appId, StringComparison.OrdinalIgnoreCase) &&
                    !id.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    !id.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) &&
                    !id.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) continue;
                var playback = session.GetPlaybackInfo();
                var isVideo = playback.PlaybackType == MediaPlaybackType.Video ||
                    fullScreen && playback.PlaybackType is MediaPlaybackType.Unknown or null;
                if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    isVideo)
                    return NativeMethods.GetForegroundWindow() == window;
            }
        }
        catch (TimeoutException) { _retryAt = Environment.TickCount64 + 5000; }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _manager = null;
            _retryAt = Environment.TickCount64 + 30_000;
        }
        return false;
    }

    private static string? GetProcessAppId(uint pid)
    {
        var process = NativeMethods.OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(130);
            var length = (uint)buffer.Capacity;
            return GetApplicationUserModelId(process, ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private static string? GetWindowAppId(nint window)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(window, ref iid, out var store) != 0) return null;
        try
        {
            // PKEY_AppUserModel_ID
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            var result = store.GetValue(ref key, out var value);
            try { return result == 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.String) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint String;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
    }

    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHGetPropertyStoreForWindow(nint window,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);
}
