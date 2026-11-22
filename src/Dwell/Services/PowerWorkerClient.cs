using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Dwell.Services;

internal sealed class PowerWorkerClient : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _closed = new();
    private Process? _worker;
    private bool _workerIsEmiOnly;
    private long _retryAt;
    private long _emiOnlyUntil;
    private long _nextDriverCheck;
    private bool _pawnIoInstalled;

    public async Task<HardwarePowerSample?> ReadAsync()
    {
        Process worker;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            lock (_sync)
            {
                if (_closed.IsCancellationRequested || Environment.TickCount64 < _retryAt) return null;
                CheckForNewCpuDriver();
                var emiOnly = Environment.TickCount64 < _emiOnlyUntil;
                if (_worker is { HasExited: true } || _worker is not null && _workerIsEmiOnly != emiOnly) StopWorker();
                worker = _worker ??= StartWorker(emiOnly);
                _workerIsEmiOnly = emiOnly;
            }
            await worker.StandardInput.WriteLineAsync("read".AsMemory(), timeout.Token);
            await worker.StandardInput.FlushAsync(timeout.Token);
            var data = await worker.StandardOutput.ReadLineAsync(timeout.Token);
            return data is not null && JsonSerializer.Deserialize<HardwarePowerSample>(data) is { } sample
                ? sample : throw new IOException("The power worker exited without a sample.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine(ex);
            lock (_sync)
            {
                StopWorker();
                _retryAt = Environment.TickCount64 + 1000;
                if (!_workerIsEmiOnly) _emiOnlyUntil = Environment.TickCount64 + 60_000;
            }
            return null;
        }
    }

    private void CheckForNewCpuDriver()
    {
        var now = Environment.TickCount64;
        if (now < _nextDriverCheck) return;
        _nextDriverCheck = now + 15_000;
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            var installed = service is not null;
            if (installed != _pawnIoInstalled)
            {
                StopWorker();
                _emiOnlyUntil = 0;
            }
            _pawnIoInstalled = installed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { Trace.WriteLine(ex); }
    }

    private static Process StartWorker(bool emiOnly)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Missing Dwell executable path.");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--power-worker");
        start.ArgumentList.Add(emiOnly ? "emi" : "native");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new IOException("Unable to start the power worker.");
    }

    private void StopWorker()
    {
        var worker = _worker;
        _worker = null;
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Trace.WriteLine(ex); }
        finally { worker.Dispose(); }
    }

    public void Dispose()
    {
        _closed.Cancel();
        lock (_sync) StopWorker();
        _closed.Dispose();
    }
}
