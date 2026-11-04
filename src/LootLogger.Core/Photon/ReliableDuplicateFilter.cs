namespace LootLogger.Core.Photon;

/// <summary>
/// Отсекает повторно отправленные надёжные команды Photon.
/// Помним номера команд только последние несколько секунд — повторы приходят именно в этом окне,
/// а новое соединение с теми же номерами не будет ошибочно отброшено.
/// </summary>
internal sealed class ReliableDuplicateFilter
{
    private static readonly long WindowTicks = TimeSpan.FromSeconds(15).Ticks;
    private const int MaxEntries = 200_000;

    private readonly Dictionary<Key, long> _seen = new();
    private readonly Queue<(Key Key, long Ticks)> _order = new();

    public bool IsDuplicate(short peerId, int challenge, byte channelId, int sequenceNumber)
    {
        var now = DateTime.UtcNow.Ticks;
        Expire(now);

        var key = new Key(peerId, challenge, channelId, sequenceNumber);
        if (_seen.ContainsKey(key))
        {
            return true;
        }

        if (_seen.Count >= MaxEntries)
        {
            _seen.Clear();
            _order.Clear();
        }

        _seen[key] = now;
        _order.Enqueue((key, now));
        return false;
    }

    private void Expire(long now)
    {
        while (_order.Count > 0 && now - _order.Peek().Ticks > WindowTicks)
        {
            var (key, ticks) = _order.Dequeue();
            if (_seen.TryGetValue(key, out var storedTicks) && storedTicks == ticks)
            {
                _seen.Remove(key);
            }
        }
    }

    private readonly record struct Key(short PeerId, int Challenge, byte ChannelId, int SequenceNumber);
}
