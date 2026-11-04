using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using Xunit;

// тест языка меняет общую настройку — запускаем тесты последовательно
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LootLogger.Tests;

public class LanguageTests
{
    [Fact]
    public void ItemNames_SwitchBetweenRussianAndEnglish()
    {
        var item = new ItemInfo(2, "T4_MAIN_SWORD@1", "Меч адепта", "Adept's Broadsword");
        var onlyRussian = new ItemInfo(3, "T5_THING", "Штука", string.Empty);
        var entry = new LootEntry { UtcTime = DateTime.UtcNow, LootedBy = "A", LootedFrom = "B", ItemIndex = 2, Quantity = 1, Item = item, ItemName = "old" };
        var unknown = new LootEntry { UtcTime = DateTime.UtcNow, LootedBy = "A", LootedFrom = "B", ItemIndex = 0, Quantity = 1, ItemName = "#999" };

        try
        {
            ItemInfo.UseEnglish = false;
            Assert.Equal("Меч адепта (T4.1)", entry.DisplayItemName);

            ItemInfo.UseEnglish = true;
            Assert.Equal("Adept's Broadsword (T4.1)", entry.DisplayItemName);
            Assert.Equal("Штука (T5.0)", onlyRussian.DisplayName); // нет английского — показываем что есть
            Assert.Equal("#999", unknown.DisplayItemName);
        }
        finally
        {
            ItemInfo.UseEnglish = false;
        }
    }
}
