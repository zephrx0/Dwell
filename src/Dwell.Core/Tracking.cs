namespace Dwell.Core;

public enum AppCategory { Communication, Productivity, Entertainment, Games, Other }

public sealed record Session(Guid Id, long AppId, DateTimeOffset Start, DateTimeOffset End);

public static class IdlePolicy
{
    public static TimeSpan Elapsed(uint tickNow, uint lastInputTick) =>
        TimeSpan.FromMilliseconds(unchecked(tickNow - lastInputTick));

    public static bool ShouldPause(TimeSpan idle, TimeSpan threshold, bool locked, bool sleeping) =>
        locked || sleeping || idle >= threshold;
}

// A session keeps its identity across checkpoints, so a flush never splits it.
public sealed class SessionAccumulator
{
    private readonly List<Session> _completed = [];
    private Session? _current;
    private DateTimeOffset? _lastSample;
    private static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(5);

    public void Sample(long? appId, DateTimeOffset now, bool paused = false)
    {
        if (_lastSample is { } last && now <= last)
        {
            CloseCurrent();
            _lastSample = now;
            return;
        }

        var continuous = _lastSample is { } previous && now - previous <= MaximumGap;
        if (_current is not null)
        {
            if (continuous) _current = _current with { End = now };
            if (!continuous || paused || _current.AppId != appId) CloseCurrent();
        }

        if (!paused && appId is { } id && _current is null)
            _current = new(Guid.NewGuid(), id, now, now);
        _lastSample = now;
    }

    public IReadOnlyList<Session> Snapshot() => _completed
        .Concat(_current is { } current && current.End > current.Start ? [current] : [])
        .ToArray();

    public void Acknowledge(IReadOnlyList<Session> written)
    {
        var ids = written.Select(x => x.Id).ToHashSet();
        _completed.RemoveAll(x => ids.Contains(x.Id));
    }

    private void CloseCurrent()
    {
        if (_current is { } current && current.End > current.Start) _completed.Add(current);
        _current = null;
    }
}
