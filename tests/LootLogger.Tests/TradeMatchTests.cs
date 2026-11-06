using LootLogger.Core.Comparator;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using Xunit;

namespace LootLogger.Tests;

public class TradeMatchTests
{
    private static readonly ItemDb Items = new(
    [
        new ItemInfo(1, "T6_MAIN_SWORD@1", "Меч", "Sword", ItemCategory.Equipment),
        new ItemInfo(2, "T6_POTION_HEAL", "Зелье", "Potion", ItemCategory.Consumable)
    ]);

    private static DateTime T(int minute) => new(2026, 11, 6, 18, minute, 0, DateTimeKind.Utc);

    private static LootEntry Loot(int minute, string by, string guild, int item, int qty, long unit = 1000) => new()
    {
        UtcTime = T(minute), LootedBy = by, LootedByGuild = guild, LootedFrom = "Victim",
        ItemIndex = item, Quantity = qty, UnitValue = unit, Item = Items.Get(item)
    };

    private static ChestLogEntry Deposit(int minute, string player, int item, int qty) => new()
    {
        UtcTime = T(minute), Player = player, ItemName = Items.DisplayName(item), Quantity = qty, Item = Items.Get(item)
    };

    private static Dictionary<string, string> Guilds(params (string Player, string Guild)[] pairs) =>
        pairs.ToDictionary(x => x.Player, x => x.Guild, StringComparer.OrdinalIgnoreCase);

    private static int Qty(PlayerComparison p, LootStatus status) => p.Items.Where(x => x.Status == status).Sum(x => x.Quantity);

    [Fact]
    public void TradedItem_DepositedByGuildmate_CountsAsDeposited()
    {
        var result = LootComparator.Compare(
            [Loot(0, "Vasya", "G", 1, 1, 50_000)],
            [Deposit(10, "Kolya", 1, 1)],
            knownGuilds: Guilds(("Kolya", "G")));

        var vasya = result.Single(x => x.Player == "Vasya");
        var item = Assert.Single(vasya.Items);
        Assert.Equal(LootStatus.DepositedByOther, item.Status);
        Assert.Equal("Kolya", item.OtherPlayer);
        Assert.Equal(0, vasya.NotDepositedValue);
        Assert.Equal(100, vasya.DepositedPercent);
        Assert.Equal(1, vasya.DepositedByOtherCount);

        var kolya = result.Single(x => x.Player == "Kolya");
        var deposit = Assert.Single(kolya.Items);
        Assert.Equal(LootStatus.DepositedForOther, deposit.Status);
        Assert.Equal("Vasya", deposit.OtherPlayer);
        Assert.Equal(0, kolya.ExtraDepositCount);
        Assert.Equal("G", kolya.Guild);
    }

    [Fact]
    public void DifferentGuild_IsNotMatched()
    {
        var result = LootComparator.Compare(
            [Loot(0, "Vasya", "G", 1, 1)],
            [Deposit(10, "Kolya", 1, 1)],
            knownGuilds: Guilds(("Kolya", "OtherGuild")));

        Assert.Equal(1, Qty(result.Single(x => x.Player == "Vasya"), LootStatus.NotDeposited));
        Assert.Equal(1, result.Single(x => x.Player == "Kolya").ExtraDepositCount);
    }

    [Fact]
    public void UnknownDepositorGuild_IsMatched()
    {
        var result = LootComparator.Compare([Loot(0, "Vasya", "G", 1, 1)], [Deposit(10, "Kolya", 1, 1)]);
        Assert.Equal(1, Qty(result.Single(x => x.Player == "Vasya"), LootStatus.DepositedByOther));
    }

    [Fact]
    public void OwnLoot_HasPriority()
    {
        var result = LootComparator.Compare(
            [Loot(0, "Vasya", "G", 1, 1), Loot(1, "Kolya", "G", 1, 1)],
            [Deposit(10, "Kolya", 1, 1)]);

        Assert.Equal(1, Qty(result.Single(x => x.Player == "Kolya"), LootStatus.Deposited));
        Assert.Equal(1, Qty(result.Single(x => x.Player == "Vasya"), LootStatus.NotDeposited));
    }

    [Fact]
    public void DepositBeforeLoot_IsNotMatched()
    {
        var result = LootComparator.Compare(
            [Loot(0, "Petya", "G", 2, 1), Loot(20, "Vasya", "G", 1, 1)],
            [Deposit(10, "Kolya", 1, 1)]);

        Assert.Equal(1, Qty(result.Single(x => x.Player == "Vasya"), LootStatus.NotDeposited));
        Assert.Equal(1, result.Single(x => x.Player == "Kolya").ExtraDepositCount);
    }

    [Fact]
    public void PartialStack_IsSplit()
    {
        var result = LootComparator.Compare(
            [Loot(0, "Vasya", "G", 2, 5, 100)],
            [Deposit(10, "Kolya", 2, 3)]);

        var vasya = result.Single(x => x.Player == "Vasya");
        Assert.Equal(3, Qty(vasya, LootStatus.DepositedByOther));
        Assert.Equal(2, Qty(vasya, LootStatus.NotDeposited));
        Assert.Equal(200, vasya.NotDepositedValue);
    }

    [Fact]
    public void DepositLargerThanTradedLoot_LeavesExtra()
    {
        var result = LootComparator.Compare(
            [Loot(0, "Vasya", "G", 2, 2)],
            [Deposit(10, "Kolya", 2, 5)]);

        var kolya = result.Single(x => x.Player == "Kolya");
        Assert.Equal(2, Qty(kolya, LootStatus.DepositedForOther));
        Assert.Equal(3, Qty(kolya, LootStatus.DepositedWithoutLoot));
    }
}
