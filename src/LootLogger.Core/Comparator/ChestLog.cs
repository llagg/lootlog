using System.Globalization;
using LootLogger.Core.Game;
using LootLogger.Core.Storage;

namespace LootLogger.Core.Comparator;

/// <summary>Строка лога гильдейского сундука: кто, когда и что положил.</summary>
public sealed class ChestLogEntry
{
    public required DateTime UtcTime { get; init; }
    public required string Player { get; init; }
    public required string ItemName { get; init; }
    public int Enchantment { get; init; }
    public int Quality { get; init; }
    public required int Quantity { get; init; }
    public ItemInfo? Item { get; init; }
}

public sealed class ChestLogParseResult
{
    public List<ChestLogEntry> Entries { get; } = new();
    public int SkippedLines { get; set; }
    public int UnknownItems { get; set; }
    public HashSet<string> UnknownItemNames { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Разбор лога сундука, скопированного из игры (кнопка копирования в окне логов сундука).
/// Колонки: дата, игрок, предмет, зачарование, качество, количество. Разделитель — табуляция (или запятая).
/// </summary>
public static class ChestLogParser
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd H:mm:ss",
        "dd.MM.yyyy H:mm:ss",
        "M/d/yyyy H:mm:ss",
        "M/d/yyyy h:mm:ss tt"
    ];

    /// <param name="timesAreUtc">Время в логе указано в UTC (так показывает игра). Иначе — в местном времени этого ПК.</param>
    public static ChestLogParseResult Parse(string text, ItemDb items, bool timesAreUtc = true)
    {
        var result = new ChestLogParseResult();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.Contains('\t') ? '\t' : ',';
            var values = LootCsv.Split(line, separator).Select(v => v.Trim().TrimStart('﻿')).ToArray();

            if (values.Length < 6 || !TryParseTime(values[0], timesAreUtc, out var utc))
            {
                // заголовок таблицы или мусор
                result.SkippedLines++;
                continue;
            }

            if (!int.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity))
            {
                result.SkippedLines++;
                continue;
            }

            int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var enchantment);
            int.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality);

            var item = items.FindByLocalizedName(values[2], enchantment);
            if (item == null)
            {
                result.UnknownItems++;
                result.UnknownItemNames.Add(values[2]);
            }

            result.Entries.Add(new ChestLogEntry
            {
                UtcTime = utc,
                Player = values[1],
                ItemName = values[2],
                Enchantment = enchantment,
                Quality = quality,
                Quantity = quantity,
                Item = item
            });
        }

        return result;
    }

    private static bool TryParseTime(string value, bool timesAreUtc, out DateTime utc)
    {
        var styles = timesAreUtc
            ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
            : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;

        return DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture, styles, out utc);
    }
}
