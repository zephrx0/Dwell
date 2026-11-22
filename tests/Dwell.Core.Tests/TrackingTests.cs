using Dwell.Core;
using Xunit;

namespace Dwell.Core.Tests;

public sealed class TrackingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ConsecutiveSamplesAndFlushKeepOneSession()
    {
        var tracker = new SessionAccumulator();
        tracker.Sample(1, Start);
        tracker.Sample(1, Start.AddSeconds(1));
        var checkpoint = tracker.Snapshot();
        tracker.Acknowledge(checkpoint);
        tracker.Sample(1, Start.AddSeconds(2));
        var session = Assert.Single(tracker.Snapshot());
        Assert.Equal(checkpoint[0].Id, session.Id);
        Assert.Equal(TimeSpan.FromSeconds(2), session.End - session.Start);
    }

    [Fact]
    public void SwitchingAndPausingDoNotMergeAcrossGaps()
    {
        var tracker = new SessionAccumulator();
        tracker.Sample(1, Start);
        tracker.Sample(2, Start.AddSeconds(1));
        tracker.Sample(null, Start.AddSeconds(2), paused: true);
        tracker.Sample(2, Start.AddSeconds(10));
        tracker.Sample(2, Start.AddSeconds(11));
        Assert.Equal(new long[] { 1, 2, 2 }, tracker.Snapshot().Select(x => x.AppId));
        Assert.All(tracker.Snapshot(), x => Assert.Equal(TimeSpan.FromSeconds(1), x.End - x.Start));
    }

    [Fact]
    public void DelayedSampleKeepsTheSameForegroundSession()
    {
        var tracker = new SessionAccumulator();
        tracker.Sample(1, Start);
        tracker.Sample(1, Start.AddSeconds(8));
        Assert.Equal(Start.AddSeconds(8), Assert.Single(tracker.Snapshot()).End);
    }

    [Fact]
    public void SleepOrClockJumpDoesNotCountUnobservedTime()
    {
        var tracker = new SessionAccumulator();
        tracker.Sample(1, Start);
        tracker.Sample(1, Start.AddSeconds(1));
        tracker.Sample(1, Start.AddHours(1));
        tracker.Sample(1, Start.AddHours(1).AddSeconds(1));
        tracker.Sample(1, Start.AddMinutes(-1));
        Assert.Equal(2, tracker.Snapshot().Count);
        Assert.All(tracker.Snapshot(), x => Assert.Equal(TimeSpan.FromSeconds(1), x.End - x.Start));
    }

    [Theory]
    [InlineData(179999, false, false, false)]
    [InlineData(180000, false, false, true)]
    [InlineData(0, true, false, true)]
    [InlineData(0, false, true, true)]
    public void IdleBoundaryAndSystemPauses(int milliseconds, bool locked, bool sleeping, bool expected) =>
        Assert.Equal(expected, IdlePolicy.ShouldPause(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMinutes(3), locked, sleeping));

    [Fact]
    public void IdleTickWrapIsUnsigned() =>
        Assert.Equal(TimeSpan.FromMilliseconds(32), IdlePolicy.Elapsed(16, uint.MaxValue - 15));
}
