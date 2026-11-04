using LootLogger.Core.Loot;

namespace LootLogger.Core.Comparator;

public enum LootStatus
{
    /// <summary>Залутал и не положил в сундук.</summary>
    NotDeposited,

    /// <summary>Залутал и положил в сундук.</summary>
    Deposited,

    /// <summary>Залутал, но потом этот предмет залутали с него самого (умер).</summary>
    Lost,

    /// <summary>Положил в сундук, но в журнале лута такого нет (своё или лут не попал в журнал).</summary>
    DepositedWithoutLoot,

    /// <summary>Положил в сундук до начала журнала — к этой сверке не относится.</summary>
    BeforeLog
}

public sealed class ComparisonItem
{
    public required string Player { get; init; }
    public LootEntry? Loot { get; init; }
    public ChestLogEntry? Deposit { get; set; }
    public LootStatus Status { get; set; }

    public DateTime? LootUtc => Loot?.UtcTime;
    public DateTime? DepositUtc => Deposit?.UtcTime;
    public string ItemName => Loot?.DisplayItemName ?? Deposit?.Item?.DisplayName ?? Deposit?.ItemName ?? string.Empty;
    public int Quantity => Loot?.Quantity ?? Deposit?.Quantity ?? 0;
    public long Value => Loot?.TotalValue ?? 0;
    public string LootedFrom => Loot?.LootedFrom ?? string.Empty;

}

public sealed class PlayerComparison
{
    public required string Player { get; init; }
    public string Guild { get; set; } = string.Empty;
    public List<ComparisonItem> Items { get; } = new();

    public int LootedCount => Items.Count(x => x.Loot != null);
    public int DepositedCount => Items.Count(x => x.Status == LootStatus.Deposited);
    public int NotDepositedCount => Items.Count(x => x.Status == LootStatus.NotDeposited);
    public int LostCount => Items.Count(x => x.Status == LootStatus.Lost);
    public int ExtraDepositCount => Items.Count(x => x.Status == LootStatus.DepositedWithoutLoot);
    public long LootedValue => Items.Where(x => x.Loot != null).Sum(x => x.Value);
    public long NotDepositedValue => Items.Where(x => x.Status == LootStatus.NotDeposited).Sum(x => x.Value);

    /// <summary>Доля сданного (по стоимости, если она известна, иначе по количеству записей).</summary>
    public double DepositedPercent
    {
        get
        {
            var counted = Items.Where(x => x.Loot != null && x.Status != LootStatus.Lost).ToList();
            if (counted.Count == 0)
            {
                return 100;
            }

            var totalValue = counted.Sum(x => x.Value);
            if (totalValue > 0)
            {
                return 100.0 * counted.Where(x => x.Status == LootStatus.Deposited).Sum(x => x.Value) / totalValue;
            }

            return 100.0 * counted.Count(x => x.Status == LootStatus.Deposited) / counted.Count;
        }
    }
}

/// <summary>
/// Сверка журнала лута с логом сундука. Каждую сдачу сопоставляем с самым свежим
/// подходящим лутом того же игрока (тот же предмет и количество, лут раньше сдачи).
/// </summary>
public static class LootComparator
{
    public static List<PlayerComparison> Compare(IReadOnlyCollection<LootEntry> loot, IReadOnlyCollection<ChestLogEntry> deposits)
    {
        var players = new Dictionary<string, PlayerComparison>(StringComparer.OrdinalIgnoreCase);

        PlayerComparison GetPlayer(string name)
        {
            if (!players.TryGetValue(name, out var player))
            {
                player = new PlayerComparison { Player = name };
                players[name] = player;
            }

            return player;
        }

        var lootItems = new List<ComparisonItem>(loot.Count);
        foreach (var entry in loot.Where(x => !string.IsNullOrWhiteSpace(x.LootedBy)))
        {
            var item = new ComparisonItem { Player = entry.LootedBy, Loot = entry, Status = LootStatus.NotDeposited };
            var player = GetPlayer(entry.LootedBy);
            if (string.IsNullOrEmpty(player.Guild))
            {
                player.Guild = entry.LootedByGuild;
            }

            player.Items.Add(item);
            lootItems.Add(item);
        }

        var candidates = lootItems
            .Where(x => x.Loot!.ItemIndex > 0)
            .GroupBy(x => (Player: x.Player.ToLowerInvariant(), x.Loot!.ItemIndex, x.Loot.Quantity))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Loot!.UtcTime).ToList());

        var firstLootUtc = lootItems.Count > 0 ? lootItems.Min(x => x.Loot!.UtcTime) : DateTime.MaxValue;

        foreach (var deposit in deposits.Where(x => x.Quantity > 0 && !string.IsNullOrWhiteSpace(x.Player)).OrderBy(x => x.UtcTime))
        {
            if (deposit.Item != null
                && candidates.TryGetValue((deposit.Player.ToLowerInvariant(), deposit.Item.Index, deposit.Quantity), out var list))
            {
                var match = list.FirstOrDefault(x => x.Deposit == null && x.Loot!.UtcTime < deposit.UtcTime);
                if (match != null)
                {
                    match.Deposit = deposit;
                    match.Status = LootStatus.Deposited;
                    continue;
                }
            }

            GetPlayer(deposit.Player).Items.Add(new ComparisonItem
            {
                Player = deposit.Player,
                Deposit = deposit,
                Status = deposit.UtcTime < firstLootUtc ? LootStatus.BeforeLog : LootStatus.DepositedWithoutLoot
            });
        }

        MarkLost(lootItems);

        return players.Values
            .OrderByDescending(x => x.NotDepositedValue)
            .ThenByDescending(x => x.NotDepositedCount)
            .ThenBy(x => x.Player, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Если игрок залутал предмет, а позже кто-то залутал тот же предмет (и количество) с него —
    /// значит, он умер с этим лутом. Такое не считаем «не сдано».
    /// </summary>
    private static void MarkLost(List<ComparisonItem> lootItems)
    {
        var lootedFromPlayer = lootItems
            .Where(x => !x.Loot!.IsFromMob && x.Loot.ItemIndex > 0)
            .GroupBy(x => (Victim: x.Loot!.LootedFrom.ToLowerInvariant(), x.Loot.ItemIndex, x.Loot.Quantity))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Loot!.UtcTime).ToList());

        foreach (var item in lootItems.Where(x => x.Status == LootStatus.NotDeposited))
        {
            var key = (item.Player.ToLowerInvariant(), item.Loot!.ItemIndex, item.Loot.Quantity);
            if (lootedFromPlayer.TryGetValue(key, out var times) && times.Any(t => t > item.Loot.UtcTime))
            {
                item.Status = LootStatus.Lost;
            }
        }
    }
}
