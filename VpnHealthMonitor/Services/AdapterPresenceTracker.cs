namespace VpnHealthMonitor.Services;

/// <summary>
/// Debounce for "the expected VPN adapter is gone".
///
/// The inventory is a snapshot of a moving target: Karing tears its TUN adapter down and recreates it on
/// reconnect, and the monitor can easily read the adapter list in that window — at Windows logon it reads
/// it before the VPN service has even started. One empty snapshot used to be enough to declare the saved
/// interface missing and paint the whole app "НУЖНА НАСТРОЙКА" until restart.
///
/// So absence has to repeat before it counts. A single sighting resets the streak.
/// </summary>
public sealed class AdapterPresenceTracker
{
    public const int DefaultThreshold = 3;

    private readonly int _threshold;
    private int _missStreak;

    public AdapterPresenceTracker(int threshold = DefaultThreshold)
    {
        _threshold = Math.Max(1, threshold);
    }

    /// <summary>True once the adapter has been missing from <see cref="_threshold"/> snapshots in a row.</summary>
    public bool ConfirmedAbsent => _missStreak >= _threshold;

    public int MissStreak => _missStreak;

    public void Observe(bool present)
    {
        if (present)
        {
            _missStreak = 0;
            return;
        }

        if (_missStreak < int.MaxValue)
        {
            _missStreak++;
        }
    }

    public void Reset() => _missStreak = 0;
}
