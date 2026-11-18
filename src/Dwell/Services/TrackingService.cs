using Dwell.Core;

namespace Dwell.Services;

public sealed record TrackingStatus(bool IsActive, bool IsPaused, string? AppKey, AppRecord? App, double ContinuousSeconds);

public sealed class TrackingService(ForegroundService foreground, AppRepository repository, SettingsService settings, BackgroundTracker background) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly SessionAccumulator _sessions = new();
    private Task? _worker;
    private volatile bool _locked = true;
    private volatile bool _sleeping;
    private volatile bool _interrupted;
    private volatile bool _manualPaused = settings.Current.TrackingPaused;
    private TrackingStatus _status = new(false, false, null, null, 0);
    public string? Error { get; private set; }
    public bool IsPaused => _manualPaused;
    public BackgroundResource[] BackgroundResources => background.Resources.Where(x => x.Key != Status.AppKey).ToArray();
    public TrackingStatus Status => Volatile.Read(ref _status);
    public event Action? PauseChanged;

    public void TogglePause()
    {
        _manualPaused = !_manualPaused;
        settings.Current.TrackingPaused = _manualPaused;
        string? saveError = null;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveError = ex.Message; }
        Interrupt();
        if (saveError is not null) Error = saveError;
        PauseChanged?.Invoke();
    }

    public void Start(bool locked) { _locked = locked; _worker = Task.Run(RunAsync); }

    public void SetLocked(bool value) { _locked = value; Interrupt(); }
    public void SetSleeping(bool value) { _sleeping = value; Interrupt(); }
    private void Interrupt()
    {
        _interrupted = true;
        // Capture before suspend; the periodic worker may not get another turn.
        _gate.Wait();
        try { _sessions.Sample(null, DateTimeOffset.UtcNow, true); background.Stop(DateTimeOffset.UtcNow); Volatile.Write(ref _status, new(false, _manualPaused, null, null, 0)); Flush(); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { Error = ex.Message; }
        finally { _gate.Release(); }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var nextWrite = Environment.TickCount64 + 30000;
        var lastTick = Environment.TickCount64;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    var tick = Environment.TickCount64;
                    var elapsed = (tick - lastTick) / 1000d;
                    lastTick = tick;
                    var paused = _manualPaused || IdlePolicy.ShouldPause(ForegroundService.IdleTime(), TimeSpan.FromMinutes(settings.Current.IdleMinutes), _locked, _sleeping);
                    var interrupted = _interrupted;
                    _interrupted = false;
                    // Slow metadata / thumbnail reads stay outside the session lock.
                    var app = paused ? null : await foreground.GetAppAsync();
                    var running = _manualPaused || _locked || _sleeping ? [] : background.RunningApps();
                    var foregroundPath = foreground.CurrentPath();
                    await _gate.WaitAsync(_stop.Token);
                    try
                    {
                        background.Sample(running, foregroundPath, DateTimeOffset.UtcNow, _manualPaused || _locked || _sleeping || interrupted || _interrupted);
                        _sessions.Sample(app is { Ignored: false } ? app.Id : null, DateTimeOffset.UtcNow, paused || interrupted || _interrupted || _locked || _sleeping);
                        var active = !paused && !interrupted && !_interrupted && !_locked && !_sleeping && !_manualPaused;
                        var continuous = active && Status.IsActive && elapsed is > 0 and <= 5 ? Status.ContinuousSeconds + elapsed : 0;
                        Volatile.Write(ref _status, new(active, _manualPaused, app is null ? null : Dwell.Core.ApplicationPolicy.Identity(app.Exe, app.Name), app, continuous));
                        if (Environment.TickCount64 >= nextWrite)
                        {
                            Flush();
                            nextWrite = Environment.TickCount64 + 30000;
                        }
                    }
                    finally { _gate.Release(); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Runtime.InteropServices.COMException)
                {
                    Error = ex.Message;
                    await _gate.WaitAsync(_stop.Token);
                    try { _sessions.Sample(null, DateTimeOffset.UtcNow, true); background.Stop(DateTimeOffset.UtcNow); Volatile.Write(ref _status, new(false, _manualPaused, null, null, 0)); }
                    finally { _gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Flush()
    {
        var batch = _sessions.Snapshot();
        var backgroundBatch = background.Snapshot();
        try { repository.WriteBatch(batch); repository.WriteBatch(backgroundBatch, background: true); _sessions.Acknowledge(batch); background.Acknowledge(backgroundBatch); Error = null; }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { Error = ex.Message; }
    }

    public async Task<UsageResult> ReadAsync(DateTimeOffset start, DateTimeOffset end, bool backgroundOnly = false)
    {
        await _gate.WaitAsync();
        IReadOnlyList<Session> pending;
        try { pending = backgroundOnly ? background.Snapshot() : _sessions.Snapshot(); }
        finally { _gate.Release(); }
        return await Task.Run(() => repository.Read(start, end, pending, backgroundOnly));
    }

    public async Task<bool> SaveAsync()
    {
        await _gate.WaitAsync();
        try { _sessions.Sample(null, DateTimeOffset.UtcNow, true); background.Stop(DateTimeOffset.UtcNow); Flush(); return Error is null; }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker;
        await SaveAsync();
    }

    public void Dispose() { _stop.Dispose(); _gate.Dispose(); }
}
