using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// One adapter snapshot is not evidence: the VPN service starts after logon and recreates its TUN on
/// every reconnect. Absence has to repeat before the app is allowed to call the route stale.
/// </summary>
public class AdapterPresenceTrackerTests
{
    [Fact]
    public void FreshTracker_DoesNotClaimAbsence()
    {
        Assert.False(new AdapterPresenceTracker().ConfirmedAbsent);
    }

    [Fact]
    public void SingleMiss_IsNotEnough()
    {
        var tracker = new AdapterPresenceTracker(threshold: 3);

        tracker.Observe(present: false);

        Assert.False(tracker.ConfirmedAbsent);
    }

    [Fact]
    public void ConsecutiveMisses_ReachTheThreshold()
    {
        var tracker = new AdapterPresenceTracker(threshold: 3);

        tracker.Observe(false);
        tracker.Observe(false);
        Assert.False(tracker.ConfirmedAbsent);

        tracker.Observe(false);
        Assert.True(tracker.ConfirmedAbsent);
    }

    [Fact]
    public void OneSighting_ResetsTheStreak()
    {
        var tracker = new AdapterPresenceTracker(threshold: 3);

        tracker.Observe(false);
        tracker.Observe(false);
        tracker.Observe(true);
        tracker.Observe(false);

        Assert.False(tracker.ConfirmedAbsent);
        Assert.Equal(1, tracker.MissStreak);
    }

    [Fact]
    public void StartupRace_ResolvesOnceTheTunnelComesUp()
    {
        // Logon order seen in the field: monitor starts, VPN service starts ~80 s later.
        var tracker = new AdapterPresenceTracker(threshold: 3);

        tracker.Observe(false);
        tracker.Observe(true);

        Assert.False(tracker.ConfirmedAbsent);
    }

    [Fact]
    public void Reset_ClearsAConfirmedAbsence()
    {
        var tracker = new AdapterPresenceTracker(threshold: 1);
        tracker.Observe(false);
        Assert.True(tracker.ConfirmedAbsent);

        tracker.Reset();

        Assert.False(tracker.ConfirmedAbsent);
    }
}
