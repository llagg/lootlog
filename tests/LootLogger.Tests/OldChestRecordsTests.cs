using LootLogger.Core.Comparator;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using Xunit;

namespace LootLogger.Tests;

/// <summary>Старые записи лога сундука (раньше первого лута в журнале) ни на что не влияют.</summary>
public class OldChestRecordsTests
{
    private static readonly ItemDb Items = new(
    [
        new ItemInfo(1, "T6_MAIN_SWORD@1", "Меч", "Sword", ItemCategory.Equipment)
    ]);

    private static DateTime T(int minute) => new(2026, 11, 6, 18, minute, 0, DateTimeKind.Utc);

    private static LootEntry Loot(int minute, string by, int qty = 1) => new()
    {
        UtcTime = T(minute), LootedBy = by, LootedByGuild = "G", LootedFrom = "Victim",
        ItemIndex = 1, Quantity = qty, UnitValue = 50_000, Item = Items.Get(1)
    };

    private static ChestLogEntry Deposit(int minute, string player, int qty = 1) => new()
    {
        UtcTime = T(minute), Player = player, ItemName = Items.DisplayName(1), Quantity = qty, Item = Items.Get(1)
    };

    [Fact]
    public void OldDeposit_DoesNotCloseLaterLoot()
    {
        // сдал меч в 18:01, а залутал такой же в 18:10 — новый меч не сдан
        var result = LootComparator.Compare([Loot(10, "Vasya")], [Deposit(1, "Vasya")]);

        var vasya = Assert.Single(result);
        Assert.Equal(1, vasya.NotDepositedCount);
        Assert.Equal(50_000, vasya.NotDepositedValue);
        Assert.Equal(0, vasya.DepositedPercent);
        Assert.Equal(0, vasya.ExtraDepositCount);
        Assert.Equal(1, vasya.OldDepositCount);
        Assert.False(vasya.HasOnlyOldDeposits);
    }

    [Fact]
    public void OldDeposit_IsNotUsedForTradeMatching()
    {
        var result = LootComparator.Compare(
            [Loot(10, "Vasya")],
            [Deposit(1, "Kolya")],
            knownGuilds: new Dictionary<string, string> { ["Kolya"] = "G" });

        var vasya = result.Single(x => x.Player == "Vasya");
        Assert.Equal(1, vasya.NotDepositedCount);
        Assert.Equal(0, vasya.DepositedByOtherCount);
    }

    [Fact]
    public void PlayerWithOnlyOldDeposits_IsLast()
    {
        var result = LootComparator.Compare(
            [Loot(10, "Vasya"), Loot(11, "Petya")],
            [Deposit(1, "Aaron", 3), Deposit(12, "Petya")]);

        Assert.Equal("Aaron", result[^1].Player);
        Assert.True(result[^1].HasOnlyOldDeposits);
        Assert.Equal(0, result[^1].ExtraDepositCount);
        Assert.Equal(0, result[^1].NotDepositedValue);
        Assert.Equal(100, result[^1].DepositedPercent);
        Assert.All(result.Take(result.Count - 1), p => Assert.False(p.HasOnlyOldDeposits));
    }
}
