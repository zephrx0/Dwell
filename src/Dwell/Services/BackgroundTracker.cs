using System.Diagnostics;
using Dwell.Core;

namespace Dwell.Services;

public sealed record BackgroundResource(string Key, long[] AppIds, double? Cpu, long Memory);

public sealed class BackgroundTracker(AppRepository repository)
{
    private readonly Dictionary<long, SessionAccumulator> _sessions = [];

    private readonly object _sync = new();
    private volatile string? _foregroundKey;
    private long _nextScan;
    private AppRecord[] _running = [];
    private readonly int _sessionId = CurrentSessionId();
    private Dictionary<int, (long Start, double Cpu)> _previous = [];
    private long _previousTick;
    private BackgroundResource[] _resources = [];
    private long _scanVersion;
    private long _storedScanVersion;
    public BackgroundResource[] Resources => Volatile.Read(ref _resources).Where(x => x.Key != _foregroundKey).ToArray();
    private static int CurrentSessionId() { using var current = Process.GetCurrentProcess(); return current.SessionId; }

    // Only inspect processes belonging to previously used applications.
    internal AppRecord[] RunningApps() { lock (_sync) return Scan(); }

    private AppRecord[] Scan()
    {
        var tick = Environment.TickCount64;
        if (tick < _nextScan) return _running;
        _nextScan = tick + 5000;
        var elapsed = (tick - _previousTick) / 1000d;
        var samples = new Dictionary<int, (long Start, double Cpu)>();
        var usage = new Dictionary<string, (HashSet<long> Ids, double Cpu, long Memory, bool HasCpu)>();
        var known = UsageProjection.Groups(repository.KnownApps()).SelectMany(x => x.Variants)
            .Where(x => !Path.GetFileName(x.Exe).Equals("Dwell.exe", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Exe, StringComparer.OrdinalIgnoreCase);
        var names = known.Keys.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, AppRecord>(StringComparer.Ordinal);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!names.Contains(process.ProcessName) || process.SessionId != _sessionId) continue;
                    var path = ForegroundService.ProcessPath((uint)process.Id);
                    if (path is null || !known.TryGetValue(path, out var app)) continue;
                    var key = ApplicationPolicy.Identity(app.Exe, app.Name);
                    running.TryAdd(key, app);
                    var start = process.StartTime.ToUniversalTime().Ticks;
                    var cpu = process.TotalProcessorTime.TotalSeconds;
                    samples[process.Id] = (start, cpu);
                    var previous = _previous.GetValueOrDefault(process.Id);
                    var valid = previous.Start == start && elapsed is > 0 and <= 10 && cpu >= previous.Cpu;
                    var delta = valid ? (cpu - previous.Cpu) / elapsed / Environment.ProcessorCount * 100 : 0;
                    var total = usage.GetValueOrDefault(key);
                    total.Ids ??= [];
                    total.Ids.Add(app.Id);
                    usage[key] = (total.Ids, total.Cpu + delta, total.Memory + process.WorkingSet64, total.HasCpu || valid);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        _previous = samples; _previousTick = tick;
        Volatile.Write(ref _resources, usage.Select(x => new BackgroundResource(x.Key, x.Value.Ids.ToArray(), x.Value.HasCpu ? Math.Clamp(x.Value.Cpu, 0, 100) : null, x.Value.Memory)).ToArray());
        _scanVersion++;
        return _running = running.Values.ToArray();
    }

    internal void Sample(AppRecord[] running, string? foregroundPath, DateTimeOffset now, bool paused)
    {
        lock (_sync) SampleCore(running, foregroundPath, now, paused);
    }

    private void SampleCore(AppRecord[] running, string? foregroundPath, DateTimeOffset now, bool paused)
    {
        var foreground = foregroundPath is null ? null : repository.Find(foregroundPath);
        var foregroundKey = foreground is null ? null : ApplicationPolicy.Identity(foreground.Exe, foreground.Name);
        _foregroundKey = foregroundKey;
        if (!paused && _storedScanVersion != _scanVersion)
        {
            repository.RecordResourcePeaks(Resources.SelectMany(x => x.AppIds.Select(id => (id, x.Cpu ?? 0d, x.Memory))));
            _storedScanVersion = _scanVersion;
        }
        var ids = paused ? [] : running.Where(x => ApplicationPolicy.Identity(x.Exe, x.Name) != foregroundKey).Select(x => x.Id).ToHashSet();
        foreach (var id in ids)
            if (!_sessions.ContainsKey(id)) _sessions.Add(id, new SessionAccumulator());
        foreach (var (id, sessions) in _sessions) sessions.Sample(ids.Contains(id) ? id : null, now, !ids.Contains(id));
    }

    internal void Stop(DateTimeOffset now)
    {
        lock (_sync)
        {
            SampleCore([], null, now, true); _nextScan = 0; _running = []; _previous.Clear();
            Volatile.Write(ref _resources, []);
        }
    }
    internal IReadOnlyList<Session> Snapshot() => _sessions.Values.SelectMany(x => x.Snapshot()).ToArray();
    internal void Acknowledge(IReadOnlyList<Session> batch)
    {
        foreach (var sessions in _sessions.Values) sessions.Acknowledge(batch);
    }
}
