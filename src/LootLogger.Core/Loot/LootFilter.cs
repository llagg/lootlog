using LootLogger.Core.Game;

namespace LootLogger.Core.Loot;

/// <summary>
/// Какие предметы показывать в журнале, статистике и сверке.
/// Экипировка — всегда; маунты и зелья с едой — по галочкам;
/// всё остальное (руны, души, ресурсы, инструменты…) — только если стопка стоит не меньше порога.
/// </summary>
public sealed class LootFilter
{
    public const long DefaultMinOtherStackValue = 50_000;

    public bool ShowMounts { get; set; }
    public bool ShowConsumables { get; set; }
    public long MinOtherStackValue { get; set; } = DefaultMinOtherStackValue;

    /// <param name="item">Предмет из справочника (null — неизвестный предмет, показываем).</param>
    /// <param name="stackValue">Стоимость всей стопки в серебре (0 — неизвестна).</param>
    public bool IsVisible(ItemInfo? item, long stackValue)
    {
        if (item == null)
        {
            return true;
        }

        return item.Category switch
        {
            ItemCategory.Equipment => true,
            ItemCategory.Mount => ShowMounts,
            ItemCategory.Consumable => ShowConsumables,
            _ => stackValue >= MinOtherStackValue
        };
    }
}
