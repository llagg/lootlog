using LootLogger.Core.Photon;

namespace LootLogger.Core.Protocol;

/// <summary>
/// Получатель разобранных игровых сообщений. Вызывается в потоке захвата,
/// поэтому реализация должна работать быстро и ничего не ждать.
/// </summary>
public interface IGameMessageSink
{
    void OnLootGrabbed(string lootedFrom, string lootedBy, bool isSilver, int itemIndex, int quantity);
    void OnItemDiscovered(long objectId, int itemIndex, int quantity, long estimatedValueInternal, int quality);
    void OnLootBody(long objectId, string name);
    void OnContainerAttached(long? objectId, Guid containerGuid, IReadOnlyList<long> slotItemIds);
    void OnLocalMoveItem(int containerSlot, Guid? containerGuid, Guid? userInteractGuid);
    void OnLocalMoveGivenItems(Guid? containerGuid, Guid? userInteractGuid, IReadOnlyList<long> itemObjectIds);
    void OnDied(string died, string diedGuild, long diedObjectId, string killedBy, string killedByGuild, long killerObjectId);
    void OnNewCharacter(long? objectId, string name, string guild, string alliance);
    void OnPartyJoined(IReadOnlyDictionary<Guid, string> members);
    void OnPartyPlayerJoined(Guid guid, string name);
    void OnPartyPlayerLeft(Guid? guid);
    void OnPartyDisbanded();
    void OnJoin(string name, string mapIndex, Guid? interactGuid, string guild, string alliance);
    void OnChangeCluster(string clusterIndex, string islandName);
}

/// <summary>
/// Разбирает Photon-пакеты Albion и передаёт дальше только сообщения, нужные лутлогеру.
/// Всё остальное отбрасывается сразу после чтения кода события.
/// </summary>
public sealed class AlbionPacketParser : PhotonParser
{
    private const byte EventCodeKey = 252;
    private const byte OperationCodeKey = 253;

    private readonly IGameMessageSink _sink;

    public AlbionPacketParser(IGameMessageSink sink)
    {
        _sink = sink;
    }

    public long HandledMessages;
    public long HandlerErrors;

    protected override void OnEvent(byte code, Dictionary<byte, object> parameters)
    {
        if (!parameters.TryGetValue(EventCodeKey, out var codeValue))
        {
            return;
        }

        var eventCode = (EventCodes) Values.ToInt(codeValue);
        try
        {
            switch (eventCode)
            {
                case EventCodes.OtherGrabbedLoot:
                    HandleGrabbedLoot(parameters);
                    break;
                case EventCodes.NewEquipmentItem:
                    HandleEquipmentItem(parameters);
                    break;
                case EventCodes.NewSimpleItem:
                case EventCodes.NewFurnitureItem:
                case EventCodes.NewJournalItem:
                case EventCodes.NewLaborerItem:
                case EventCodes.NewKillTrophyItem:
                case EventCodes.NewSiegeBannerItem:
                    HandleSimpleItem(parameters);
                    break;
                case EventCodes.NewLoot:
                    HandleNewLoot(parameters);
                    break;
                case EventCodes.NewLootChest:
                    HandleNewLootChest(parameters);
                    break;
                case EventCodes.AttachItemContainer:
                    HandleAttachItemContainer(parameters);
                    break;
                case EventCodes.Died:
                    HandleDied(parameters);
                    break;
                case EventCodes.NewCharacter:
                    HandleNewCharacter(parameters);
                    break;
                case EventCodes.PartyJoined:
                    HandlePartyJoined(parameters);
                    break;
                case EventCodes.PartyPlayerJoined:
                    HandlePartyPlayerJoined(parameters);
                    break;
                case EventCodes.PartyPlayerLeft:
                    _sink.OnPartyPlayerLeft(parameters.TryGetValue(1, out var leftGuid) ? Values.ToGuid(leftGuid) : null);
                    break;
                case EventCodes.PartyDisbanded:
                    _sink.OnPartyDisbanded();
                    break;
                default:
                    return;
            }

            HandledMessages++;
        }
        catch
        {
            HandlerErrors++;
        }
    }

    protected override void OnRequest(byte operationCode, Dictionary<byte, object> parameters)
    {
        if (!parameters.TryGetValue(OperationCodeKey, out var codeValue))
        {
            return;
        }

        try
        {
            switch ((OperationCodes) Values.ToInt(codeValue))
            {
                case OperationCodes.InventoryMoveItem:
                    _sink.OnLocalMoveItem(
                        parameters.TryGetValue(0, out var slot) ? Values.ToInt(slot) : -1,
                        parameters.TryGetValue(1, out var containerGuid) ? Values.ToGuid(containerGuid) : null,
                        parameters.TryGetValue(4, out var interactGuid) ? Values.ToGuid(interactGuid) : null);
                    break;
                case OperationCodes.InventoryMoveGivenItems:
                    _sink.OnLocalMoveGivenItems(
                        parameters.TryGetValue(0, out var givenContainerGuid) ? Values.ToGuid(givenContainerGuid) : null,
                        parameters.TryGetValue(2, out var givenInteractGuid) ? Values.ToGuid(givenInteractGuid) : null,
                        parameters.TryGetValue(4, out var itemIds) ? Values.ToLongList(itemIds) : []);
                    break;
                default:
                    return;
            }

            HandledMessages++;
        }
        catch
        {
            HandlerErrors++;
        }
    }

    protected override void OnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object> parameters)
    {
        if (!parameters.TryGetValue(OperationCodeKey, out var codeValue))
        {
            return;
        }

        try
        {
            switch ((OperationCodes) Values.ToInt(codeValue))
            {
                case OperationCodes.Join:
                    _sink.OnJoin(
                        parameters.TryGetValue(2, out var name) ? Values.ToText(name) : string.Empty,
                        parameters.TryGetValue(8, out var mapIndex) ? Values.ToText(mapIndex) : string.Empty,
                        parameters.TryGetValue(54, out var interactGuid) ? Values.ToGuid(interactGuid) : null,
                        parameters.TryGetValue(58, out var guild) ? Values.ToText(guild) : string.Empty,
                        parameters.TryGetValue(79, out var alliance) ? Values.ToText(alliance) : string.Empty);
                    break;
                case OperationCodes.ChangeCluster:
                    _sink.OnChangeCluster(
                        parameters.TryGetValue(0, out var cluster) ? Values.ToText(cluster) : string.Empty,
                        parameters.TryGetValue(2, out var island) ? Values.ToText(island) : string.Empty);
                    break;
                default:
                    return;
            }

            HandledMessages++;
        }
        catch
        {
            HandlerErrors++;
        }
    }

    private void HandleGrabbedLoot(Dictionary<byte, object> p)
    {
        _sink.OnLootGrabbed(
            p.TryGetValue(1, out var from) ? Values.ToText(from) : string.Empty,
            p.TryGetValue(2, out var by) ? Values.ToText(by) : string.Empty,
            p.TryGetValue(3, out var silver) && Values.ToBool(silver),
            p.TryGetValue(4, out var item) ? Values.ToInt(item) : 0,
            p.TryGetValue(5, out var quantity) ? Values.ToInt(quantity) : 0);
    }

    private void HandleEquipmentItem(Dictionary<byte, object> p)
    {
        if (!p.TryGetValue(0, out var objectIdValue) || Values.ToLong(objectIdValue) is not { } objectId)
        {
            return;
        }

        // В новых версиях игры после поля 5 (имя крафтера) все поля сдвинуты на одну позицию.
        var shifted = p.TryGetValue(6, out var six) && six is string;
        var qualityKey = shifted ? (byte) 7 : (byte) 6;

        _sink.OnItemDiscovered(
            objectId,
            p.TryGetValue(1, out var item) ? Values.ToInt(item) : 0,
            p.TryGetValue(2, out var quantity) ? Values.ToInt(quantity) : 0,
            p.TryGetValue(4, out var value) ? Values.ToLong(value) ?? 0 : 0,
            p.TryGetValue(qualityKey, out var quality) ? Values.ToInt(quality) : 0);
    }

    private void HandleSimpleItem(Dictionary<byte, object> p)
    {
        if (!p.TryGetValue(0, out var objectIdValue) || Values.ToLong(objectIdValue) is not { } objectId)
        {
            return;
        }

        _sink.OnItemDiscovered(
            objectId,
            p.TryGetValue(1, out var item) ? Values.ToInt(item) : 0,
            p.TryGetValue(2, out var quantity) ? Values.ToInt(quantity) : 0,
            p.TryGetValue(4, out var value) ? Values.ToLong(value) ?? 0 : 0,
            0);
    }

    private void HandleNewLoot(Dictionary<byte, object> p)
    {
        if (p.TryGetValue(0, out var objectIdValue) && Values.ToLong(objectIdValue) is { } objectId)
        {
            _sink.OnLootBody(objectId, p.TryGetValue(3, out var body) ? Values.ToText(body) : string.Empty);
        }
    }

    private void HandleNewLootChest(Dictionary<byte, object> p)
    {
        if (p.TryGetValue(0, out var objectIdValue) && long.TryParse(Values.ToText(objectIdValue), out var objectId))
        {
            _sink.OnLootBody(objectId, p.TryGetValue(3, out var name) ? Values.ToText(name) : string.Empty);
        }
    }

    private void HandleAttachItemContainer(Dictionary<byte, object> p)
    {
        var containerGuid = p.TryGetValue(1, out var guidValue) ? Values.ToGuid(guidValue) ?? Guid.Empty : Guid.Empty;
        _sink.OnContainerAttached(
            p.TryGetValue(0, out var objectId) ? Values.ToLong(objectId) : null,
            containerGuid,
            p.TryGetValue(3, out var slots) ? Values.ToLongList(slots) : []);
    }

    private void HandleDied(Dictionary<byte, object> p)
    {
        _sink.OnDied(
            p.TryGetValue(2, out var died) ? Values.ToText(died) : string.Empty,
            p.TryGetValue(3, out var diedGuild) ? Values.ToText(diedGuild) : string.Empty,
            p.TryGetValue(1, out var diedId) ? Values.ToLong(diedId) ?? 0 : 0,
            p.TryGetValue(10, out var killer) ? Values.ToText(killer) : string.Empty,
            p.TryGetValue(11, out var killerGuild) ? Values.ToText(killerGuild) : string.Empty,
            p.TryGetValue(9, out var killerId) ? Values.ToLong(killerId) ?? 0 : 0);
    }

    private void HandleNewCharacter(Dictionary<byte, object> p)
    {
        _sink.OnNewCharacter(
            p.TryGetValue(0, out var objectId) ? Values.ToLong(objectId) : null,
            p.TryGetValue(1, out var name) ? Values.ToText(name) : string.Empty,
            p.TryGetValue(8, out var guild) ? Values.ToText(guild) : string.Empty,
            p.TryGetValue(51, out var alliance) && alliance is string allianceName ? allianceName : string.Empty);
    }

    private void HandlePartyJoined(Dictionary<byte, object> p)
    {
        if (!p.TryGetValue(8, out var guidsValue) || !p.TryGetValue(9, out var namesValue))
        {
            return;
        }

        var guids = Values.ToGuidList(guidsValue);
        var names = Values.ToStringList(namesValue);
        var members = new Dictionary<Guid, string>();
        for (var i = 0; i < Math.Min(guids.Count, names.Count); i++)
        {
            if (guids[i] != Guid.Empty && !string.IsNullOrWhiteSpace(names[i]))
            {
                members[guids[i]] = names[i];
            }
        }

        _sink.OnPartyJoined(members);
    }

    private void HandlePartyPlayerJoined(Dictionary<byte, object> p)
    {
        var guid = p.TryGetValue(1, out var guidValue) ? Values.ToGuid(guidValue) ?? Guid.Empty : Guid.Empty;
        var name = p.TryGetValue(2, out var nameValue) ? Values.ToText(nameValue) : string.Empty;
        if (guid != Guid.Empty && !string.IsNullOrWhiteSpace(name))
        {
            _sink.OnPartyPlayerJoined(guid, name);
        }
    }
}
