using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

public sealed class RollingHealthWindow
{
    private readonly int _maxItems;
    private readonly Queue<NetworkSnapshot> _items = new();

    public RollingHealthWindow(int maxItems)
    {
        _maxItems = Math.Max(1, maxItems);
    }

    public void Add(NetworkSnapshot snapshot)
    {
        _items.Enqueue(snapshot);

        while (_items.Count > _maxItems)
        {
            _items.Dequeue();
        }
    }

    public void Clear()
    {
        _items.Clear();
    }

    public double? AveragePingMs
    {
        get
        {
            var pings = _items
                .Where(item => item.PingAverageMs.HasValue)
                .Select(item => item.PingAverageMs!.Value)
                .ToList();

            return pings.Count == 0 ? null : pings.Average();
        }
    }

    public double PacketLossPercent
    {
        get
        {
            if (_items.Count == 0)
            {
                return 100;
            }

            return _items.Average(item => item.PacketLossPercent);
        }
    }

    public int Count => _items.Count;
}
