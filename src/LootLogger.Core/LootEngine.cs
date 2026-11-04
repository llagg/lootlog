using LootLogger.Core.Capture;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using LootLogger.Core.Protocol;

namespace LootLogger.Core;

/// <summary>
/// Связывает всё вместе: захват → разбор Photon → логика лута.
/// Входящие и исходящие пакеты разбираются раздельно (у них свои номера команд и фрагменты).
/// </summary>
public sealed class LootEngine : IDisposable
{
    private readonly AlbionPacketParser _incoming;
    private readonly AlbionPacketParser _outgoing;
    private readonly NpcapCapture _capture;

    public LootEngine(GameDatabase game, PriceBook prices)
    {
        Prices = prices;
        Tracker = new LootTracker(game, prices);
        _incoming = new AlbionPacketParser(Tracker);
        _outgoing = new AlbionPacketParser(Tracker);
        _capture = new NpcapCapture(OnPayload);
    }

    public LootTracker Tracker { get; }
    public PriceBook Prices { get; }
    public NpcapCapture Capture => _capture;

    public long ParserErrors => _incoming.HandlerErrors + _outgoing.HandlerErrors + _capture.HandlerErrors;
    public long DuplicateCommands => _incoming.DuplicateReliableCommands + _outgoing.DuplicateReliableCommands;

    public void Start(string adapterId)
    {
        _capture.Start(adapterId);
    }

    public void Stop()
    {
        _capture.Stop();
    }

    private void OnPayload(ReadOnlySpan<byte> payload, bool incoming)
    {
        (incoming ? _incoming : _outgoing).ReceivePacket(payload);
    }

    public void Dispose()
    {
        _capture.Dispose();
    }
}
