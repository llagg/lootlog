using System.Text;
using LootLogger.Core.Game;

namespace LootLogger.Core.Loot;

/// <summary>
/// Оценочная рыночная стоимость предметов, которую присылает сервер игры вместе с данными предмета.
/// Храним последнюю оценку для каждого качества и сохраняем между запусками.
/// </summary>
public sealed class PriceBook
{
    public const long InternalFactor = 10000;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, Dictionary<int, long>> _valuesByItem = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _valuesByItem.Count;
            }
        }
    }

    public static long ToSilver(long internalValue)
    {
        return internalValue / InternalFactor;
    }

    public void Record(int itemIndex, int quality, long internalValue)
    {
        if (itemIndex <= 0 || internalValue <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_valuesByItem.TryGetValue(itemIndex, out var byQuality))
            {
                byQuality = new Dictionary<int, long>(1);
                _valuesByItem[itemIndex] = byQuality;
            }

            byQuality[quality] = ToSilver(internalValue);
        }
    }

    /// <summary>
    /// Стоимость одной штуки предмета без учёта качества. Если оценок несколько,
    /// берём ту, что ближе всего к среднему (крайние значения отбрасываются) — так же, как оригинальный SAT.
    /// </summary>
    public long GetUnitValue(int itemIndex)
    {
        List<long> values;
        lock (_lock)
        {
            if (!_valuesByItem.TryGetValue(itemIndex, out var byQuality) || byQuality.Count == 0)
            {
                return 0;
            }

            values = byQuality.Values.Where(v => v > 0).ToList();
        }

        return NearestToAverage(values);
    }

    internal static long NearestToAverage(List<long> values)
    {
        switch (values.Count)
        {
            case 0:
                return 0;
            case < 3:
                return values.Min();
        }

        values.Sort();
        var trimmed = values.Skip(1).Take(values.Count - 2).ToList();
        var average = trimmed.Average(v => (double) v);
        return trimmed.OrderBy(v => Math.Abs(v - average)).First();
    }

    public void Load(string path, ItemDb items)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            lock (_lock)
            {
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 3 || !int.TryParse(parts[1], out var quality) || !long.TryParse(parts[2], out var silver))
                    {
                        continue;
                    }

                    var item = items.FindByUniqueName(parts[0]);
                    if (item == null || silver <= 0)
                    {
                        continue;
                    }

                    if (!_valuesByItem.TryGetValue(item.Index, out var byQuality))
                    {
                        byQuality = new Dictionary<int, long>(1);
                        _valuesByItem[item.Index] = byQuality;
                    }

                    // свежие значения из текущей сессии важнее сохранённых
                    byQuality.TryAdd(quality, silver);
                }
            }
        }
        catch
        {
            // повреждённый файл цен — просто начинаем с нуля
        }
    }

    public void Save(string path, ItemDb items)
    {
        try
        {
            var lines = new List<string>();
            lock (_lock)
            {
                foreach (var (itemIndex, byQuality) in _valuesByItem)
                {
                    var item = items.Get(itemIndex);
                    if (item == null)
                    {
                        continue;
                    }

                    foreach (var (quality, silver) in byQuality)
                    {
                        lines.Add($"{item.UniqueName}\t{quality}\t{silver}");
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        catch
        {
            // не критично
        }
    }
}
