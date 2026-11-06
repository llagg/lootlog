using System.Text;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using Xunit;

namespace LootLogger.Tests;

public class FilterTests
{
    private static readonly Dictionary<string, (string Ru, string En)> NoNames = new();

    [Fact]
    public void Categories_AreReadFromSlotType()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <items>
              <weapon uniquename="T6_MAIN_SWORD" slottype="mainhand" shopcategory="melee">
                <enchantments><enchantment enchantmentlevel="1" /></enchantments>
              </weapon>
              <equipmentitem uniquename="T6_HEAD_PLATE_SET1" slottype="head" shopcategory="armor" />
              <equipmentitem uniquename="T6_BAG" slottype="bag" shopcategory="accessories" />
              <equipmentitem uniquename="T6_CAPE" slottype="cape" shopcategory="accessories" />
              <equipmentitem uniquename="T6_OFF_SHIELD" slottype="offhand" shopcategory="offhand" />
              <mount uniquename="T5_MOUNT_ARMORED_HORSE" slottype="mount" shopcategory="mounts" />
              <consumableitem uniquename="T6_POTION_HEAL" slottype="potion" shopcategory="consumables" />
              <consumableitem uniquename="T7_MEAL_STEW" slottype="food" shopcategory="consumables" />
              <equipmentitem uniquename="T6_2H_TOOL_PICK" slottype="mainhand" shopcategory="tools" />
              <equipmentitem uniquename="T5_2H_TOOL_TRACKING" slottype="mainhand" />
              <simpleitem uniquename="T4_RUNE" shopcategory="materials" />
              <simpleitem uniquename="T6_ARTEFACT_MAIN_SCIMITAR_MORGANA" shopcategory="artefacts" />
              <journalitem uniquename="T4_JOURNAL_WARRIOR" />
            </items>
            """;

        var items = GameDataLoader.ParseItemsXml(Encoding.UTF8.GetBytes(xml), NoNames)
            .ToDictionary(x => x.UniqueName, x => x.Category);

        Assert.Equal(ItemCategory.Equipment, items["T6_MAIN_SWORD"]);
        Assert.Equal(ItemCategory.Equipment, items["T6_MAIN_SWORD@1"]); // зачарованные — как основной предмет
        Assert.Equal(ItemCategory.Equipment, items["T6_HEAD_PLATE_SET1"]);
        Assert.Equal(ItemCategory.Equipment, items["T6_BAG"]);
        Assert.Equal(ItemCategory.Equipment, items["T6_CAPE"]);
        Assert.Equal(ItemCategory.Equipment, items["T6_OFF_SHIELD"]);
        Assert.Equal(ItemCategory.Mount, items["T5_MOUNT_ARMORED_HORSE"]);
        Assert.Equal(ItemCategory.Consumable, items["T6_POTION_HEAL"]);
        Assert.Equal(ItemCategory.Consumable, items["T7_MEAL_STEW"]);
        Assert.Equal(ItemCategory.Other, items["T6_2H_TOOL_PICK"]);
        Assert.Equal(ItemCategory.Other, items["T5_2H_TOOL_TRACKING"]);
        Assert.Equal(ItemCategory.Other, items["T4_RUNE"]);
        Assert.Equal(ItemCategory.Other, items["T6_ARTEFACT_MAIN_SCIMITAR_MORGANA"]);
        Assert.Equal(ItemCategory.Other, items["T4_JOURNAL_WARRIOR_FULL"]);
    }

    [Fact]
    public void Filter_Rules()
    {
        var sword = new ItemInfo(1, "T6_MAIN_SWORD", "Меч", "Sword", ItemCategory.Equipment);
        var horse = new ItemInfo(2, "T5_MOUNT_HORSE", "Лошадь", "Horse", ItemCategory.Mount);
        var potion = new ItemInfo(3, "T6_POTION_HEAL", "Зелье", "Potion", ItemCategory.Consumable);
        var rune = new ItemInfo(4, "T4_RUNE", "Руна", "Rune", ItemCategory.Other);

        var filter = new LootFilter();

        // экипировка всегда, даже дешёвая и без цены
        Assert.True(filter.IsVisible(sword, 0));
        Assert.True(filter.IsVisible(sword, 100));

        // по умолчанию маунты и расходники скрыты — даже дорогие
        Assert.False(filter.IsVisible(horse, 1_000_000));
        Assert.False(filter.IsVisible(potion, 1_000_000));
        filter.ShowMounts = true;
        filter.ShowConsumables = true;
        Assert.True(filter.IsVisible(horse, 0));
        Assert.True(filter.IsVisible(potion, 0));

        // прочее — по стоимости всей стопки
        Assert.False(filter.IsVisible(rune, 0));
        Assert.False(filter.IsVisible(rune, 49_999));
        Assert.True(filter.IsVisible(rune, 50_000));
        Assert.True(filter.IsVisible(rune, 20 * 3_000)); // 20 рун по 3к = 60к

        // неизвестный предмет не прячем
        Assert.True(filter.IsVisible(null, 0));
    }
}
