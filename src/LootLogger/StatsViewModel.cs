using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media;
using LootLogger.Core.Comparator;
using LootLogger.Core.Loot;

namespace LootLogger;

public sealed record GuildOption(string Key, string Display);

/// <summary>Один предмет у игрока: иконка с количеством, стоимость, подсказка, цвет по сверке.</summary>
public sealed class ItemStat : ObservableObject
{
    public ItemStat(int itemIndex, string uniqueName)
    {
        ItemIndex = itemIndex;
        UniqueName = uniqueName;
        if (!string.IsNullOrEmpty(uniqueName))
        {
            IconCache.Instance.Request(uniqueName, image => Icon = image);
        }
    }

    public int ItemIndex { get; }
    public string UniqueName { get; }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    private string _shortLabel = string.Empty;
    /// <summary>Подпись вместо картинки, пока иконки нет: тир и уровень.</summary>
    public string ShortLabel { get => _shortLabel; set => Set(ref _shortLabel, value); }

    private int _quantity;
    public int Quantity
    {
        get => _quantity;
        set
        {
            if (Set(ref _quantity, value))
            {
                OnPropertyChanged(nameof(QuantityBadge));
            }
        }
    }

    public string QuantityBadge => _quantity > 1 ? $"×{_quantity}" : string.Empty;

    private long _value;
    public long Value { get => _value; set => Set(ref _value, value); }

    private LootStatus? _status;
    /// <summary>Итог сверки с сундуком (null — сверки не было).</summary>
    public LootStatus? Status { get => _status; set => Set(ref _status, value); }

    private string _toolTip = string.Empty;
    public string ToolTipText { get => _toolTip; set => Set(ref _toolTip, value); }
}

public sealed class PlayerStatsRow : ObservableObject
{
    public PlayerStatsRow(string player)
    {
        Player = player;
    }

    public string Player { get; }
    public ObservableCollection<ItemStat> Items { get; } = new();

    private int _rank;
    public int Rank { get => _rank; set => Set(ref _rank, value); }

    private string _guild = string.Empty;
    public string Guild { get => _guild; set => Set(ref _guild, value); }

    private long _totalValue;
    public long TotalValue { get => _totalValue; set => Set(ref _totalValue, value); }

    private string _totals = string.Empty;
    public string Totals { get => _totals; set => Set(ref _totals, value); }

    private string _comparison = string.Empty;
    /// <summary>Строка итогов сверки (пусто, если сверки не было).</summary>
    public string Comparison { get => _comparison; set => Set(ref _comparison, value); }
}

/// <summary>
/// Вкладка «Статистика игроков»: кто сколько залутал, предметы иконками.
/// Строки и иконки обновляются на месте, а не пересоздаются — список не мигает и картинки не перезагружаются.
/// </summary>
public sealed class StatsViewModel : ObservableObject
{
    public const string AllGuilds = "\u0001all";
    public const string NoGuild = "\u0001none";

    private Dictionary<(string Player, int Item), LootStatus>? _comparison;
    private Dictionary<string, (long NotDepositedValue, double Percent)>? _comparisonTotals;

    public ObservableCollection<PlayerStatsRow> Rows { get; } = new();
    public ObservableCollection<GuildOption> Guilds { get; } = new();

    public event Action? FilterChanged;

    private GuildOption? _selectedGuild;
    public GuildOption? SelectedGuild
    {
        get => _selectedGuild;
        set
        {
            if (value != null && Set(ref _selectedGuild, value))
            {
                FilterChanged?.Invoke();
            }
        }
    }

    private string _summary = string.Empty;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Запомнить итог сверки, чтобы раскрасить иконки.</summary>
    public void SetComparison(IReadOnlyCollection<PlayerComparison> results)
    {
        // у одного предмета может быть несколько строк сверки — показываем самую проблемную
        var map = new Dictionary<(string, int), LootStatus>();
        foreach (var player in results)
        {
            foreach (var item in player.Items.Where(x => x.Loot != null))
            {
                var key = (player.Player.ToLowerInvariant(), item.Loot!.ItemIndex);
                map[key] = map.TryGetValue(key, out var existing) ? (LootStatus) Math.Min((int) existing, (int) item.Status) : item.Status;
            }
        }

        _comparison = map;
        _comparisonTotals = results.ToDictionary(x => x.Player.ToLowerInvariant(), x => (x.NotDepositedValue, x.DepositedPercent));
    }

    public void ClearComparison()
    {
        _comparison = null;
        _comparisonTotals = null;
    }

    /// <summary>После смены языка названия и подсказки нужно собрать заново.</summary>
    public void Reset()
    {
        Rows.Clear();
        Guilds.Clear();
        _selectedGuild = null;
    }

    public void Update(IReadOnlyList<LootEntry> entries)
    {
        var culture = Loc.Instance.Culture;

        UpdateGuilds(entries);
        var guildKey = _selectedGuild?.Key ?? AllGuilds;

        var byPlayer = entries
            .Where(e => MatchesGuild(guildKey, e.LootedByGuild))
            .GroupBy(e => e.LootedBy, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Player = g.First().LootedBy,
                Guild = g.Select(x => x.LootedByGuild).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? string.Empty,
                Entries = g.ToList(),
                Total = g.Sum(x => x.TotalValue),
                Count = g.Sum(x => x.Quantity)
            })
            .OrderByDescending(x => x.Total)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Player, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = Rows.ToDictionary(r => r.Player, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(byPlayer.Select(x => x.Player), StringComparer.OrdinalIgnoreCase);

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Rows[i].Player))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var index = 0; index < byPlayer.Count; index++)
        {
            var data = byPlayer[index];
            if (!existing.TryGetValue(data.Player, out var row))
            {
                row = new PlayerStatsRow(data.Player);
            }

            row.Rank = index + 1;
            row.Guild = data.Guild;
            row.TotalValue = data.Total;
            row.Totals = string.Format(culture, Loc.T("StatsTotals"), data.Total, data.Count, data.Entries.Count);
            row.Comparison = _comparisonTotals != null && _comparisonTotals.TryGetValue(data.Player.ToLowerInvariant(), out var cmp)
                ? string.Format(culture, Loc.T("StatsComparison"), cmp.Percent, cmp.NotDepositedValue)
                : string.Empty;
            UpdateItems(row, data.Entries);

            var currentIndex = Rows.IndexOf(row);
            if (currentIndex < 0)
            {
                Rows.Insert(index, row);
            }
            else if (currentIndex != index)
            {
                Rows.Move(currentIndex, index);
            }
        }

        var totalValue = byPlayer.Sum(x => x.Total);
        Summary = byPlayer.Count == 0
            ? Loc.T("StatsEmpty")
            : string.Format(culture, Loc.T("StatsSummary"), byPlayer.Count, totalValue);
    }

    private void UpdateItems(PlayerStatsRow row, List<LootEntry> entries)
    {
        var culture = Loc.Instance.Culture;
        var groups = entries
            .GroupBy(e => e.ItemIndex > 0 ? e.UniqueName : "#" + e.ItemName)
            .Select(g => new { Key = g.Key, Entries = g.OrderByDescending(x => x.UtcTime).ToList(), Value = g.Sum(x => x.TotalValue), Quantity = g.Sum(x => x.Quantity) })
            .OrderByDescending(g => g.Value)
            .ThenByDescending(g => g.Quantity)
            .ToList();

        var existing = row.Items.ToDictionary(x => x.UniqueName.Length > 0 ? x.UniqueName : "#" + x.ShortLabel, StringComparer.OrdinalIgnoreCase);
        var wanted = groups.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = row.Items.Count - 1; i >= 0; i--)
        {
            var key = row.Items[i].UniqueName.Length > 0 ? row.Items[i].UniqueName : "#" + row.Items[i].ShortLabel;
            if (!wanted.Contains(key))
            {
                row.Items.RemoveAt(i);
            }
        }

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var first = group.Entries[0];
            if (!existing.TryGetValue(group.Key, out var stat))
            {
                stat = new ItemStat(first.ItemIndex, first.ItemIndex > 0 ? first.UniqueName : string.Empty);
            }

            stat.Quantity = group.Quantity;
            stat.Value = group.Value;
            stat.ShortLabel = first.Item is { Tier: > 0 } item ? $"T{item.Tier}.{item.Enchantment}" : first.DisplayItemName;
            stat.Status = _comparison != null && _comparison.TryGetValue((row.Player.ToLowerInvariant(), first.ItemIndex), out var status) ? status : null;
            stat.ToolTipText = BuildToolTip(first.DisplayItemName, group.Quantity, group.Value, group.Entries, stat.Status, culture);

            var currentIndex = row.Items.IndexOf(stat);
            if (currentIndex < 0)
            {
                row.Items.Insert(index, stat);
            }
            else if (currentIndex != index)
            {
                row.Items.Move(currentIndex, index);
            }
        }
    }

    private static string BuildToolTip(string name, int quantity, long value, List<LootEntry> entries, LootStatus? status, IFormatProvider culture)
    {
        const int maxLines = 8;
        var sb = new StringBuilder();
        sb.AppendLine(name);
        sb.AppendLine(string.Format(culture, Loc.T("StatsTipTotals"), quantity, value));
        if (status != null)
        {
            sb.AppendLine(LootStatusConverter.Translate(status.Value));
        }

        foreach (var entry in entries.Take(maxLines))
        {
            sb.AppendLine(string.Format(culture, Loc.T("StatsTipLine"), entry.Quantity, SourceNameConverter.Translate(entry.LootedFrom), entry.LocalTime));
        }

        if (entries.Count > maxLines)
        {
            sb.AppendLine(string.Format(culture, Loc.T("StatsTipMore"), entries.Count - maxLines));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Список для выбора гильдии: «Все», «Без гильдии» (если есть такие игроки) и гильдии по алфавиту.</summary>
    public static List<GuildOption> BuildGuildOptions(IEnumerable<string> playerGuilds)
    {
        var all = playerGuilds.ToList();
        var guilds = all
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var options = new List<GuildOption> { new(AllGuilds, Loc.T("AllGuilds")) };
        if (all.Any(string.IsNullOrEmpty))
        {
            options.Add(new GuildOption(NoGuild, Loc.T("NoGuild")));
        }

        options.AddRange(guilds.Select(g => new GuildOption(g, g)));
        return options;
    }

    public static bool MatchesGuild(string? key, string guild)
    {
        return key is null or AllGuilds
               || (key == NoGuild ? string.IsNullOrEmpty(guild) : string.Equals(guild, key, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateGuilds(IReadOnlyList<LootEntry> entries)
    {
        var wanted = BuildGuildOptions(entries.Select(e => e.LootedByGuild));

        if (wanted.SequenceEqual(Guilds))
        {
            _selectedGuild ??= Guilds.FirstOrDefault();
            return;
        }

        var selectedKey = _selectedGuild?.Key ?? AllGuilds;
        Guilds.Clear();
        foreach (var option in wanted)
        {
            Guilds.Add(option);
        }

        // выбор гильдии сохраняем, даже если список пополнился
        _selectedGuild = Guilds.FirstOrDefault(g => g.Key == selectedKey) ?? Guilds[0];
        OnPropertyChanged(nameof(SelectedGuild));
    }
}
