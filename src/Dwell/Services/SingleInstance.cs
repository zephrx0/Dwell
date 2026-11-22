using System.IO.MemoryMappedFiles;
using System.Security.Principal;

namespace Dwell.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly Mutex? _uninstallMutex;
    private readonly nint _showEvent;
    private readonly MemoryMappedFile? _owner;
    private readonly bool _owns;
    public bool IsFirst => _owns;
    private CancellationTokenSource? _stop;
    private Task? _listener;

    public SingleInstance()
    {
        var name = "Local\\Dwell." + WindowsIdentity.GetCurrent().User!.Value;
        _mutex = new(true, name, out _owns);
        if (_owns)
        {
            _uninstallMutex = new(false, "Local\\Dwell.Uninstall");
            _owner = MemoryMappedFile.CreateOrOpen(name + ".Owner", sizeof(int));
            using var view = _owner.CreateViewAccessor(0, sizeof(int));
            view.Write(0, Environment.ProcessId);
        }
        _showEvent = NativeMethods.CreateEvent(0, false, false, name + ".Show");
        if (!_owns && !Environment.GetCommandLineArgs().Contains("--background"))
        {
            try
            {
                using var owner = MemoryMappedFile.OpenExisting(name + ".Owner", MemoryMappedFileRights.Read);
                using var view = owner.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Read);
                var pid = view.ReadInt32(0);
                if (pid > 0) NativeMethods.AllowSetForegroundWindow((uint)pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            NativeMethods.SetEvent(_showEvent);
        }
    }

    public void Listen(Action show)
    {
        _stop = new();
        _listener = Task.Run(() =>
        {
            while (!_stop.IsCancellationRequested)
                if (NativeMethods.WaitForSingleObject(_showEvent, 1000) == 0) show();
        });
    }

    public void Dispose()
    {
        _stop?.Cancel(); _listener?.GetAwaiter().GetResult(); _stop?.Dispose();
        NativeMethods.CloseHandle(_showEvent);
        _owner?.Dispose();
        _uninstallMutex?.Dispose();
        if (_owns) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
