using System.Globalization;
using System.Text;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;

namespace LootLogger.Core.Storage;

/// <summary>
/// Формат CSV журнала. Первые 18 колонок совпадают с экспортом Statistics Analysis Tool,
/// поэтому файлы можно открывать в SAT и наоборот. Колонка 19 (in_party) — наша.
/// </summary>
public static class LootCsv
{
    public static readonly string[] Columns =
    [
        "timestamp_utc", "looted_by__alliance", "looted_by__guild", "looted_by__name", "item_id", "item_name", "quantity",
        "looted_from__alliance", "looted_from__guild", "looted_from__name", "died", "died_player_guild", "killed_by", "killed_by_guild",
        "average_est_market_value", "cluster", "died_player_alliance", "killed_by_alliance", "in_party"
    ];

    private const int SatColumnCount = 18;
    private const char Separator = ';';

    public static string Header => string.Join(Separator, Columns);

    public static string Format(LootEntry e)
    {
        return Join(
            e.UtcTime.ToString("O", CultureInfo.InvariantCulture),
            e.LootedByAlliance, e.LootedByGuild, e.LootedBy,
            e.UniqueName, e.DisplayItemName, e.Quantity.ToString(CultureInfo.InvariantCulture),
            e.LootedFromAlliance, e.LootedFromGuild, e.LootedFrom,
            string.Empty, string.Empty, string.Empty, string.Empty,
            e.UnitValue.ToString(CultureInfo.InvariantCulture),
            e.Cluster, string.Empty, string.Empty,
            e.IsPartyRelated ? "1" : "0");
    }

    public static string Format(KillEntry k)
    {
        return Join(
            k.UtcTime.ToString("O", CultureInfo.InvariantCulture),
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty,
            k.Died, k.DiedGuild, k.KilledBy, k.KilledByGuild,
            string.Empty, k.Cluster, k.DiedAlliance, k.KilledByAlliance, string.Empty);
    }

    public static void Write(string path, IEnumerable<LootEntry> entries)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(Header);
        foreach (var entry in entries)
        {
            writer.WriteLine(Format(entry));
        }
    }

    public sealed class ReadResult
    {
        public List<LootEntry> Loot { get; } = new();
        public List<KillEntry> Kills { get; } = new();
        public int SkippedLines { get; set; }
        public bool IsValid { get; set; }
    }

    /// <summary>Читает файл журнала (наш или экспорт SAT).</summary>
    public static ReadResult Read(string path, ItemDb items)
    {
        var result = new ReadResult();
        var fileName = Path.GetFileName(path);
        var headerChecked = false;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = Split(line, Separator);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = values[i].Trim().TrimStart('﻿');
            }

            if (!headerChecked)
            {
                headerChecked = true;
                result.IsValid = values.Length >= SatColumnCount
                                 && Columns.Take(SatColumnCount).Select((c, i) => string.Equals(values[i], c, StringComparison.OrdinalIgnoreCase)).All(x => x);
                if (!result.IsValid)
                {
                    return result;
                }

                continue;
            }

            if (values.Length < 10 || !TryParseTime(values[0], out var utc))
            {
                result.SkippedLines++;
                continue;
            }

            var isKillRow = string.IsNullOrWhiteSpace(values[3]) && values.Length >= 14;
            if (isKillRow)
            {
                if (!string.IsNullOrWhiteSpace(values[10]))
                {
                    result.Kills.Add(new KillEntry
                    {
                        UtcTime = utc,
                        Died = values[10],
                        DiedGuild = values[11],
                        KilledBy = values[12],
                        KilledByGuild = values[13],
                        Cluster = Get(values, 15),
                        DiedAlliance = Get(values, 16),
                        KilledByAlliance = Get(values, 17)
                    });
                }
                else
                {
                    result.SkippedLines++;
                }

                continue;
            }

            if (!int.TryParse(values[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            {
                result.SkippedLines++;
                continue;
            }

            var item = ResolveItem(items, values[4], values[5]);
            long.TryParse(Get(values, 14).Split('.', ',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitValue);
            var lootedFrom = NormalizeImportedSource(values[9]);

            result.Loot.Add(new LootEntry
            {
                UtcTime = utc,
                LootedByAlliance = values[1],
                LootedByGuild = values[2],
                LootedBy = values[3],
                UniqueName = item?.UniqueName ?? values[4],
                ItemName = item?.DisplayName ?? (string.IsNullOrWhiteSpace(values[5]) ? values[4] : values[5]),
                ItemIndex = item?.Index ?? 0,
                Item = item,
                Quantity = quantity,
                LootedFromAlliance = values[7],
                LootedFromGuild = values[8],
                LootedFrom = lootedFrom,
                IsFromMob = lootedFrom is LootTracker.MobName or LootTracker.ChestName,
                UnitValue = Math.Max(0, unitValue),
                Cluster = Get(values, 15),
                IsPartyRelated = Get(values, 18) == "1",
                SourceFile = fileName
            });
        }

        return result;
    }

    private static string NormalizeImportedSource(string name)
    {
        return name.Trim().ToUpperInvariant() switch
        {
            "MOB" or "МОБ" or "MONSTER" => LootTracker.MobName,
            "СУНДУК" or "CHEST" => LootTracker.ChestName,
            _ => LootTracker.NormalizeSource(name)
        };
    }

    private static ItemInfo? ResolveItem(ItemDb items, string identifier, string name)
    {
        if (int.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return items.Get(index);
        }

        return items.FindByUniqueName(identifier)
               ?? items.FindByLocalizedName(name, ItemInfo.ParseEnchantment(identifier));
    }

    public static bool TryParseTime(string value, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var offset))
        {
            utc = offset.UtcDateTime;
            return true;
        }

        utc = default;
        return false;
    }

    private static string Get(string[] values, int index)
    {
        return index < values.Length ? values[index] : string.Empty;
    }

    private static string Join(params string[] values)
    {
        var sb = new StringBuilder(256);
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(Separator);
            }

            sb.Append(Escape(values[i]));
        }

        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Contains(Separator) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>Разбивает строку CSV с учётом кавычек.</summary>
    public static string[] Split(string line, char separator)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == separator && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
