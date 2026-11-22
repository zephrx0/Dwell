using System.Security.Principal;

namespace Dwell.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly Mutex? _uninstallMutex;
    private readonly nint _showEvent;
    private readonly bool _owns;
    public bool IsFirst => _owns;
    private CancellationTokenSource? _stop;
    private Task? _listener;

    public SingleInstance()
    {
        var name = "Local\\Dwell." + WindowsIdentity.GetCurrent().User!.Value;
        _mutex = new(true, name, out _owns);
        if (_owns) _uninstallMutex = new(false, "Local\\Dwell.Uninstall");
        _showEvent = NativeMethods.CreateEvent(0, false, false, name + ".Show");
        if (!_owns && !Environment.GetCommandLineArgs().Contains("--background"))
        {
            NativeMethods.AllowSetForegroundWindow(uint.MaxValue);
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
        _uninstallMutex?.Dispose();
        if (_owns) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
