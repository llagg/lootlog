using LootLogger.Core.Game;
using LootLogger.Core.Protocol;

namespace LootLogger.Core.Loot;

/// <summary>
/// Вся логика лутлогера. Получает разобранные сообщения игры (в одном потоке — потоке захвата),
/// хранит минимум состояния и выдаёт готовые записи через события <see cref="LootRecorded"/> и <see cref="KillRecorded"/>.
/// Никаких обращений к интерфейсу и диску здесь нет — поэтому захват пакетов никогда не ждёт.
/// </summary>
public sealed class LootTracker : IGameMessageSink
{
    /// <summary>Служебные имена источника лута; в интерфейсе подменяются переводом.</summary>
    public const string MobName = "MOB";
    public const string ChestName = "CHEST";

    private const int MaxDiscoveredItems = 50_000;
    private const int MaxKnownCharacters = 50_000;
    private static readonly TimeSpan LocalLootMatchWindow = TimeSpan.FromSeconds(5);

    private readonly PriceBook _prices;
    private readonly Func<DateTime> _utcNow;

    private readonly Dictionary<long, DiscoveredItem> _discoveredItems = new();
    private readonly Dictionary<long, string> _lootBodies = new();
    private readonly HashSet<long> _recordedLocalItemIds = new();
    private readonly Dictionary<string, (string Guild, string Alliance)> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _partyMembers = new();
    private readonly HashSet<string> _partyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime Utc, int ItemIndex, int Quantity, string From, bool FromLocalPath)> _recentLocalLoot = new();

    private volatile GameDatabase _game;
    private Container? _currentContainer;
    private Guid? _localInteractGuid;

    public LootTracker(GameDatabase game, PriceBook prices, Func<DateTime>? utcNow = null)
    {
        _game = game;
        _prices = prices;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public event Action<LootEntry>? LootRecorded;
    public event Action<KillEntry>? KillRecorded;
    public event Action? StateChanged;

    public string LocalPlayerName { get; private set; } = string.Empty;
    public string LocalGuild { get; private set; } = string.Empty;
    public string LocalAlliance { get; private set; } = string.Empty;
    public string CurrentCluster { get; private set; } = string.Empty;

    public int PartySize
    {
        get
        {
            lock (_partyNames)
            {
                return _partyNames.Count;
            }
        }
    }

    public GameDatabase Game
    {
        get => _game;
        set => _game = value;
    }

    #region Лут

    public void OnLootGrabbed(string lootedFrom, string lootedBy, bool isSilver, int itemIndex, int quantity)
    {
        if (isSilver || itemIndex <= 0 || quantity <= 0 || string.IsNullOrWhiteSpace(lootedBy))
        {
            return;
        }

        var from = NormalizeSource(lootedFrom);

        // Свой лут мы уже могли записать точнее (по объекту в контейнере) — не дублируем.
        if (IsLocalPlayer(lootedBy) && TryConsumeRecentLocalLoot(itemIndex, quantity, from, fromLocalPath: false))
        {
            return;
        }

        if (IsLocalPlayer(lootedBy))
        {
            RememberRecentLocalLoot(itemIndex, quantity, from, fromLocalPath: false);
        }

        Record(lootedBy, from, itemIndex, quantity, _prices.GetUnitValue(itemIndex));
    }

    public void OnItemDiscovered(long objectId, int itemIndex, int quantity, long estimatedValueInternal, int quality)
    {
        if (itemIndex <= 0)
        {
            return;
        }

        _prices.Record(itemIndex, quality, estimatedValueInternal);

        if (_discoveredItems.Count >= MaxDiscoveredItems)
        {
            _discoveredItems.Clear();
        }

        _discoveredItems[objectId] = new DiscoveredItem(itemIndex, quantity, estimatedValueInternal);
    }

    public void OnLootBody(long objectId, string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _lootBodies[objectId] = name;
        }
    }

    public void OnContainerAttached(long? objectId, Guid containerGuid, IReadOnlyList<long> slotItemIds)
    {
        _currentContainer = new Container(objectId, containerGuid, slotItemIds);
    }

    public void OnLocalMoveItem(int containerSlot, Guid? containerGuid, Guid? userInteractGuid)
    {
        if (!TryGetCurrentLootBody(containerGuid, userInteractGuid, out var container, out var bodyName))
        {
            return;
        }

        if (containerSlot < 0 || containerSlot >= container.SlotItemIds.Count)
        {
            return;
        }

        RecordLocalLoot(container.SlotItemIds[containerSlot], bodyName);
    }

    public void OnLocalMoveGivenItems(Guid? containerGuid, Guid? userInteractGuid, IReadOnlyList<long> itemObjectIds)
    {
        if (itemObjectIds.Count == 0 || !TryGetCurrentLootBody(containerGuid, userInteractGuid, out var container, out var bodyName))
        {
            return;
        }

        var inContainer = container.SlotItemIds.ToHashSet();
        foreach (var itemObjectId in itemObjectIds.Distinct())
        {
            if (inContainer.Contains(itemObjectId))
            {
                RecordLocalLoot(itemObjectId, bodyName);
            }
        }
    }

    private bool TryGetCurrentLootBody(Guid? containerGuid, Guid? userInteractGuid, out Container container, out string bodyName)
    {
        container = default!;
        bodyName = string.Empty;

        var current = _currentContainer;
        if (current is null || containerGuid is null || userInteractGuid is null || string.IsNullOrEmpty(LocalPlayerName))
        {
            return false;
        }

        if (_localInteractGuid != userInteractGuid || current.ContainerGuid != containerGuid)
        {
            return false;
        }

        if (current.ObjectId is not { } objectId || !_lootBodies.TryGetValue(objectId, out var name))
        {
            return false;
        }

        container = current;
        bodyName = name;
        return true;
    }

    private void RecordLocalLoot(long itemObjectId, string bodyName)
    {
        if (itemObjectId <= 0 || !_discoveredItems.TryGetValue(itemObjectId, out var item) || !_recordedLocalItemIds.Add(itemObjectId))
        {
            return;
        }

        var from = NormalizeSource(bodyName);
        if (TryConsumeRecentLocalLoot(item.ItemIndex, item.Quantity, from, fromLocalPath: true))
        {
            return;
        }

        RememberRecentLocalLoot(item.ItemIndex, item.Quantity, from, fromLocalPath: true);

        var unitValue = item.EstimatedValueInternal > 0
            ? PriceBook.ToSilver(item.EstimatedValueInternal)
            : _prices.GetUnitValue(item.ItemIndex);

        Record(LocalPlayerName, from, item.ItemIndex, item.Quantity, unitValue);
    }

    private void Record(string lootedBy, string lootedFrom, int itemIndex, int quantity, long unitValue)
    {
        var item = _game.Items.Get(itemIndex);
        if (item?.IsTrash == true)
        {
            return;
        }

        var looter = GetCharacter(lootedBy);
        var victim = GetCharacter(lootedFrom);
        var isFromMob = lootedFrom is MobName or ChestName;

        var entry = new LootEntry
        {
            UtcTime = _utcNow(),
            LootedBy = lootedBy,
            LootedByGuild = looter.Guild,
            LootedByAlliance = looter.Alliance,
            LootedFrom = lootedFrom,
            LootedFromGuild = isFromMob ? string.Empty : victim.Guild,
            LootedFromAlliance = isFromMob ? string.Empty : victim.Alliance,
            IsFromMob = isFromMob,
            ItemIndex = itemIndex,
            UniqueName = item?.UniqueName ?? string.Empty,
            ItemName = item?.DisplayName ?? $"#{itemIndex}",
            Item = item,
            Quantity = quantity,
            UnitValue = unitValue,
            Cluster = CurrentCluster,
            IsPartyRelated = IsPartyRelated(lootedBy) || (!isFromMob && IsPartyRelated(lootedFrom))
        };

        LootRecorded?.Invoke(entry);
    }

    private void RememberRecentLocalLoot(int itemIndex, int quantity, string from, bool fromLocalPath)
    {
        var now = _utcNow();
        _recentLocalLoot.RemoveAll(x => now - x.Utc > LocalLootMatchWindow);
        _recentLocalLoot.Add((now, itemIndex, quantity, from, fromLocalPath));
    }

    /// <summary>
    /// Свой лут игра может прислать двумя путями: запросом перемещения предмета из контейнера
    /// и общим событием «игрок подобрал предмет». Засчитываем такую пару один раз.
    /// </summary>
    private bool TryConsumeRecentLocalLoot(int itemIndex, int quantity, string from, bool fromLocalPath)
    {
        var now = _utcNow();
        _recentLocalLoot.RemoveAll(x => now - x.Utc > LocalLootMatchWindow);

        var index = _recentLocalLoot.FindIndex(x =>
            x.FromLocalPath != fromLocalPath
            && x.ItemIndex == itemIndex
            && x.Quantity == quantity
            && string.Equals(x.From, from, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return false;
        }

        _recentLocalLoot.RemoveAt(index);
        return true;
    }

    /// <summary>Имена мобов и сундуков приводим к общему виду, имена игроков оставляем как есть.</summary>
    public static string NormalizeSource(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ChestName;
        }

        if (name.Contains("@MOB", StringComparison.OrdinalIgnoreCase))
        {
            return MobName;
        }

        // В именах игроков Albion нет «@» и «_», а в служебных именах сундуков они есть.
        if (name.Contains('@') || name.Contains('_'))
        {
            return ChestName;
        }

        return name;
    }

    #endregion

    #region Игроки, группа, локация

    public void OnDied(string died, string diedGuild, long diedObjectId, string killedBy, string killedByGuild, long killerObjectId)
    {
        if (string.IsNullOrWhiteSpace(died) || died.Contains('@'))
        {
            return;
        }

        KillRecorded?.Invoke(new KillEntry
        {
            UtcTime = _utcNow(),
            Died = died,
            DiedGuild = diedGuild,
            DiedAlliance = GetCharacter(died).Alliance,
            KilledBy = NormalizeSource(killedBy) is MobName or ChestName ? MobName : killedBy,
            KilledByGuild = killedByGuild,
            KilledByAlliance = GetCharacter(killedBy).Alliance,
            Cluster = CurrentCluster
        });
    }

    public void OnNewCharacter(long? objectId, string name, string guild, string alliance)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_characters.Count >= MaxKnownCharacters)
        {
            _characters.Clear();
        }

        _characters[name] = (guild, alliance);
    }

    public void OnPartyJoined(IReadOnlyDictionary<Guid, string> members)
    {
        lock (_partyNames)
        {
            _partyMembers.Clear();
            _partyNames.Clear();
            foreach (var (guid, name) in members)
            {
                _partyMembers[guid] = name;
                _partyNames.Add(name);
            }
        }

        StateChanged?.Invoke();
    }

    public void OnPartyPlayerJoined(Guid guid, string name)
    {
        lock (_partyNames)
        {
            _partyMembers[guid] = name;
            _partyNames.Add(name);
        }

        StateChanged?.Invoke();
    }

    public void OnPartyPlayerLeft(Guid? guid)
    {
        if (guid is null)
        {
            return;
        }

        lock (_partyNames)
        {
            if (_partyMembers.Remove(guid.Value, out var name))
            {
                _partyNames.Remove(name);
            }

            // если ушли мы сами — группы больше нет
            if (string.Equals(name, LocalPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                _partyMembers.Clear();
                _partyNames.Clear();
            }
        }

        StateChanged?.Invoke();
    }

    public void OnPartyDisbanded()
    {
        lock (_partyNames)
        {
            _partyMembers.Clear();
            _partyNames.Clear();
        }

        StateChanged?.Invoke();
    }

    public void OnJoin(string name, string mapIndex, Guid? interactGuid, string guild, string alliance)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            LocalPlayerName = name;
            LocalGuild = guild;
            LocalAlliance = alliance;
            _characters[name] = (guild, alliance);
        }

        _localInteractGuid = interactGuid;
        ResetZoneState();
        CurrentCluster = _game.ClusterDisplayName(mapIndex);
        StateChanged?.Invoke();
    }

    public void OnChangeCluster(string clusterIndex, string islandName)
    {
        ResetZoneState();
        if (!string.IsNullOrWhiteSpace(clusterIndex))
        {
            CurrentCluster = _game.ClusterDisplayName(clusterIndex, islandName);
        }

        StateChanged?.Invoke();
    }

    public bool IsInParty(string name)
    {
        lock (_partyNames)
        {
            return _partyNames.Contains(name);
        }
    }

    private bool IsPartyRelated(string name)
    {
        return IsLocalPlayer(name) || IsInParty(name);
    }

    private bool IsLocalPlayer(string name)
    {
        return !string.IsNullOrEmpty(LocalPlayerName) && string.Equals(name, LocalPlayerName, StringComparison.OrdinalIgnoreCase);
    }

    private (string Guild, string Alliance) GetCharacter(string name)
    {
        return !string.IsNullOrEmpty(name) && _characters.TryGetValue(name, out var info) ? info : (string.Empty, string.Empty);
    }

    /// <summary>Объекты в игре живут в пределах одной зоны — при смене зоны их можно забыть.</summary>
    private void ResetZoneState()
    {
        _discoveredItems.Clear();
        _lootBodies.Clear();
        _recordedLocalItemIds.Clear();
        _currentContainer = null;
    }

    #endregion

    private readonly record struct DiscoveredItem(int ItemIndex, int Quantity, long EstimatedValueInternal);

    private sealed record Container(long? ObjectId, Guid ContainerGuid, IReadOnlyList<long> SlotItemIds);
}
