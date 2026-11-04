namespace LootLogger.Core.Loot;

/// <summary>Одна запись журнала: кто, что, с кого и когда залутал.</summary>
public sealed class LootEntry
{
    public required DateTime UtcTime { get; init; }
    public required string LootedBy { get; init; }
    public string LootedByGuild { get; init; } = string.Empty;
    public string LootedByAlliance { get; init; } = string.Empty;
    public required string LootedFrom { get; init; }
    public string LootedFromGuild { get; init; } = string.Empty;
    public string LootedFromAlliance { get; init; } = string.Empty;

    /// <summary>Лут не с игрока, а с моба или сундука.</summary>
    public bool IsFromMob { get; init; }

    public required int ItemIndex { get; init; }
    public string UniqueName { get; init; } = string.Empty;
    /// <summary>Название на момент записи (для предметов, которых нет в справочнике).</summary>
    public string ItemName { get; init; } = string.Empty;

    public Game.ItemInfo? Item { get; init; }

    /// <summary>Название на текущем языке интерфейса.</summary>
    public string DisplayItemName => Item?.DisplayName ?? ItemName;
    public required int Quantity { get; init; }

    /// <summary>Оценочная стоимость одной штуки в серебре (0 — неизвестна).</summary>
    public long UnitValue { get; init; }

    public long TotalValue => UnitValue * Quantity;

    public string Cluster { get; init; } = string.Empty;

    /// <summary>Лутающий или его жертва были в вашей группе в момент лута.</summary>
    public bool IsPartyRelated { get; init; }

    /// <summary>Пусто для живой записи, иначе — имя файла, из которого запись загружена.</summary>
    public string SourceFile { get; init; } = string.Empty;

    public DateTime LocalTime => UtcTime.ToLocalTime();
}

/// <summary>Запись о смерти игрока.</summary>
public sealed class KillEntry
{
    public required DateTime UtcTime { get; init; }
    public required string Died { get; init; }
    public string DiedGuild { get; init; } = string.Empty;
    public string DiedAlliance { get; init; } = string.Empty;
    public required string KilledBy { get; init; }
    public string KilledByGuild { get; init; } = string.Empty;
    public string KilledByAlliance { get; init; } = string.Empty;
    public string Cluster { get; init; } = string.Empty;
}
