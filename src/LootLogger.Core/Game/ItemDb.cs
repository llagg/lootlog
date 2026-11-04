namespace LootLogger.Core.Game;

public sealed class ItemInfo
{
    public ItemInfo(int index, string uniqueName, string nameRu, string nameEn)
    {
        Index = index;
        UniqueName = uniqueName;
        NameRu = nameRu;
        NameEn = nameEn;
        Tier = ParseTier(uniqueName);
        Enchantment = ParseEnchantment(uniqueName);
        IsTrash = uniqueName.Contains("TRASH", StringComparison.Ordinal);

        _displayRu = WithTier(!string.IsNullOrWhiteSpace(nameRu) ? nameRu : !string.IsNullOrWhiteSpace(nameEn) ? nameEn : uniqueName);
        _displayEn = WithTier(!string.IsNullOrWhiteSpace(nameEn) ? nameEn : !string.IsNullOrWhiteSpace(nameRu) ? nameRu : uniqueName);
    }

    private readonly string _displayRu;
    private readonly string _displayEn;

    /// <summary>Показывать названия предметов на английском (переключается в интерфейсе).</summary>
    public static bool UseEnglish { get; set; }

    private string WithTier(string name)
    {
        return Tier > 0 ? $"{name} (T{Tier}.{Enchantment})" : name;
    }

    public int Index { get; }
    public string UniqueName { get; }
    public string NameRu { get; }
    public string NameEn { get; }
    public int Tier { get; }
    public int Enchantment { get; }
    public bool IsTrash { get; }

    /// <summary>Название для показа на выбранном языке с уровнем, например «Меч мастера (T6.1)».</summary>
    public string DisplayName => UseEnglish ? _displayEn : _displayRu;

    public static int ParseTier(string uniqueName)
    {
        return uniqueName.Length >= 2 && uniqueName[0] == 'T' && char.IsAsciiDigit(uniqueName[1])
            ? uniqueName[1] - '0'
            : 0;
    }

    public static int ParseEnchantment(string uniqueName)
    {
        var at = uniqueName.LastIndexOf('@');
        return at >= 0 && int.TryParse(uniqueName.AsSpan(at + 1), out var level) ? level : 0;
    }
}

/// <summary>Справочник предметов: индекс из сетевых пакетов → предмет.</summary>
public sealed class ItemDb
{
    private readonly ItemInfo?[] _byIndex;
    private readonly Dictionary<string, ItemInfo> _byUniqueName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, int Enchantment), ItemInfo> _byLocalizedName = new();

    public static ItemDb Empty { get; } = new([]);

    public ItemDb(IReadOnlyCollection<ItemInfo> items)
    {
        var maxIndex = items.Count == 0 ? 0 : items.Max(x => x.Index);
        _byIndex = new ItemInfo?[maxIndex + 1];

        foreach (var item in items)
        {
            _byIndex[item.Index] = item;
            _byUniqueName.TryAdd(item.UniqueName, item);

            AddLocalizedName(item.NameRu, item);
            AddLocalizedName(item.NameEn, item);
        }

        Count = items.Count;
    }

    public int Count { get; }

    public ItemInfo? Get(int index)
    {
        return index > 0 && index < _byIndex.Length ? _byIndex[index] : null;
    }

    public ItemInfo? FindByUniqueName(string uniqueName)
    {
        return string.IsNullOrWhiteSpace(uniqueName) ? null : _byUniqueName.GetValueOrDefault(uniqueName.Trim());
    }

    /// <summary>Поиск по названию из лога сундука (русскому или английскому) и уровню зачарования.</summary>
    public ItemInfo? FindByLocalizedName(string name, int enchantment)
    {
        return string.IsNullOrWhiteSpace(name)
            ? null
            : _byLocalizedName.GetValueOrDefault((NormalizeName(name), enchantment));
    }

    /// <summary>Подпись для предмета, даже если его нет в справочнике.</summary>
    public string DisplayName(int index)
    {
        return Get(index)?.DisplayName ?? $"#{index}";
    }

    private void AddLocalizedName(string name, ItemInfo item)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _byLocalizedName.TryAdd((NormalizeName(name), item.Enchantment), item);
    }

    private static string NormalizeName(string name)
    {
        return name.Trim().Replace('ё', 'е').Replace('Ё', 'Е').ToLowerInvariant();
    }
}
