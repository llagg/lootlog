using LootLogger.Core.Loot;

namespace LootLogger.Core.Comparator;

/// <summary>Порядок значений задаёт порядок строк в таблице сверки: сначала проблемные.</summary>
public enum LootStatus
{
    /// <summary>Залутал и не положил в сундук.</summary>
    NotDeposited,

    /// <summary>Залутал, потом погиб, и никто (в зоне видимости) не залутал это с его тела. Возможно, уничтожено при смерти.</summary>
    DiedWithLoot,

    /// <summary>Залутал, но потом этот предмет залутали с него самого.</summary>
    Lost,

    /// <summary>
    /// Лут не сдан самим игроком, но такой же предмет позже сдал другой игрок той же гильдии
    /// (скорее всего, через трейд). Считается сданным.
    /// </summary>
    DepositedByOther,

    /// <summary>Залутал и положил в сундук.</summary>
    Deposited,

    /// <summary>Положил в сундук, но в журнале лута такого нет (своё или лут не попал в журнал).</summary>
    DepositedWithoutLoot,

    /// <summary>Положил в сундук предмет, который залутал другой игрок (пара к <see cref="DepositedByOther"/>).</summary>
    DepositedForOther,

    /// <summary>Положил в сундук до начала журнала — к этой сверке не относится.</summary>
    BeforeLog
}

/// <summary>
/// Строка сверки. Одна запись лута может распасться на несколько строк:
/// например, из стопки в 5 зелий 3 сдано, а 2 нет.
/// </summary>
public sealed class ComparisonItem
{
    public required string Player { get; init; }
    public LootEntry? Loot { get; init; }
    public ChestLogEntry? Deposit { get; init; }
    public LootStatus Status { get; init; }
    public required int Quantity { get; init; }

    /// <summary>Второй игрок для «Сдано другим» / «Сдал за другого».</summary>
    public string OtherPlayer { get; init; } = string.Empty;

    public DateTime? LootUtc => Loot?.UtcTime;
    public DateTime? DepositUtc => Deposit?.UtcTime;
    public string ItemName => Loot?.DisplayItemName ?? Deposit?.Item?.DisplayName ?? Deposit?.ItemName ?? string.Empty;
    public long Value => (Loot?.UnitValue ?? 0) * Quantity;

    /// <summary>Засчитывается как сданное (самим игроком или за него).</summary>
    public bool CountsAsDeposited => Status is LootStatus.Deposited or LootStatus.DepositedByOther;
    public string LootedFrom => Loot?.LootedFrom ?? string.Empty;
}

public sealed class PlayerComparison
{
    public required string Player { get; init; }
    public string Guild { get; set; } = string.Empty;
    public List<ComparisonItem> Items { get; } = new();

    /// <summary>Сколько записей лута (не штук) у игрока.</summary>
    public int LootedCount => Items.Where(x => x.Loot != null).Select(x => x.Loot).Distinct().Count();
    public int DepositedCount => Count(LootStatus.Deposited);
    public int DepositedByOtherCount => Count(LootStatus.DepositedByOther);
    public int NotDepositedCount => Count(LootStatus.NotDeposited);
    public int LostCount => Count(LootStatus.Lost);
    public int DiedWithLootCount => Count(LootStatus.DiedWithLoot);
    public int ExtraDepositCount => Count(LootStatus.DepositedWithoutLoot);
    public long LootedValue => Items.Where(x => x.Loot != null).Sum(x => x.Value);
    public long NotDepositedValue => Items.Where(x => x.Status == LootStatus.NotDeposited).Sum(x => x.Value);

    /// <summary>
    /// Доля сданного по стоимости (или по количеству строк, если стоимость неизвестна).
    /// Потерянное и то, с чем игрок погиб, не учитывается — это не его вина.
    /// </summary>
    public double DepositedPercent
    {
        get
        {
            var counted = Items.Where(x => x.Loot != null && (x.CountsAsDeposited || x.Status == LootStatus.NotDeposited)).ToList();
            if (counted.Count == 0)
            {
                return 100;
            }

            var totalValue = counted.Sum(x => x.Value);
            if (totalValue > 0)
            {
                return 100.0 * counted.Where(x => x.CountsAsDeposited).Sum(x => x.Value) / totalValue;
            }

            return 100.0 * counted.Count(x => x.CountsAsDeposited) / counted.Count;
        }
    }

    private int Count(LootStatus status) => Items.Count(x => x.Status == status);
}

/// <summary>
/// Сверка журнала лута с логом сундука.
///
/// Восстанавливаем события по времени и для каждого игрока ведём «что у него на руках из залутанного»:
/// <list type="bullet">
/// <item>лут добавляет предметы лутающему и забирает их у того, с кого залутали (это «Потеряно»);</item>
/// <item>смерть помечает всё, что у игрока на руках, как «при смерти»;</item>
/// <item>сдача в сундук забирает предметы с рук (сначала самые свежие).</item>
/// </list>
/// Всё считается поштучно, поэтому стопки сверяются по сумме, а одна запись лута с тела
/// закрывает ровно столько штук, сколько с него забрали.
///
/// Вторым проходом сдачи, для которых у сдавшего нет своего лута, ищут не сданный лут
/// другого игрока той же гильдии (предмет передали трейдом) — «Сдано другим игроком».
/// </summary>
public static class LootComparator
{
    public static List<PlayerComparison> Compare(
        IReadOnlyCollection<LootEntry> loot,
        IReadOnlyCollection<ChestLogEntry> deposits,
        IReadOnlyCollection<KillEntry>? kills = null,
        IReadOnlyDictionary<string, string>? knownGuilds = null)
    {
        var players = new Dictionary<string, PlayerComparison>(StringComparer.OrdinalIgnoreCase);
        var holdings = new Dictionary<(string Player, int Item), List<Lot>>();
        var lots = new List<Lot>();
        var extras = new List<(ChestLogEntry Deposit, int Quantity, LootStatus Status)>();
        var guilds = CollectGuilds(loot, knownGuilds);

        PlayerComparison GetPlayer(string name)
        {
            if (!players.TryGetValue(name, out var player))
            {
                player = new PlayerComparison { Player = name };
                players[name] = player;
            }

            return player;
        }

        List<Lot> Holding(string player, int item)
        {
            var key = (player.ToLowerInvariant(), item);
            if (!holdings.TryGetValue(key, out var list))
            {
                list = new List<Lot>();
                holdings[key] = list;
            }

            return list;
        }

        var validLoot = loot.Where(x => !string.IsNullOrWhiteSpace(x.LootedBy) && x.Quantity > 0).ToList();
        var firstLootUtc = validLoot.Count > 0 ? validLoot.Min(x => x.UtcTime) : DateTime.MaxValue;

        // Все события в одну ленту. При равном времени: лут → смерть → сдача.
        var events = new List<(DateTime Utc, int Order, object Event)>();
        events.AddRange(validLoot.Select(x => (x.UtcTime, 0, (object) x)));
        events.AddRange((kills ?? Array.Empty<KillEntry>()).Where(x => !string.IsNullOrWhiteSpace(x.Died)).Select(x => (x.UtcTime, 1, (object) x)));
        events.AddRange(deposits.Where(x => x.Quantity > 0 && !string.IsNullOrWhiteSpace(x.Player)).Select(x => (x.UtcTime, 2, (object) x)));

        foreach (var (_, _, item) in events.OrderBy(x => x.Utc).ThenBy(x => x.Order))
        {
            switch (item)
            {
                case LootEntry entry:
                {
                    // С тела игрока забрали предмет — у него этих штук больше нет.
                    if (!entry.IsFromMob && entry.ItemIndex > 0)
                    {
                        Take(Holding(entry.LootedFrom, entry.ItemIndex), entry.Quantity, preferDied: true, (lot, n) => lot.Lost += n);
                    }

                    var lot = new Lot(entry);
                    lots.Add(lot);
                    if (entry.ItemIndex > 0)
                    {
                        Holding(entry.LootedBy, entry.ItemIndex).Add(lot);
                    }

                    var player = GetPlayer(entry.LootedBy);
                    if (string.IsNullOrEmpty(player.Guild))
                    {
                        player.Guild = entry.LootedByGuild;
                    }

                    break;
                }

                case KillEntry kill:
                {
                    var died = kill.Died.ToLowerInvariant();
                    foreach (var (key, list) in holdings)
                    {
                        if (key.Player != died)
                        {
                            continue;
                        }

                        foreach (var lot in list.Where(x => x.Remaining > 0))
                        {
                            lot.Died = true;
                        }
                    }

                    break;
                }

                case ChestLogEntry deposit:
                {
                    var missing = deposit.Quantity;
                    if (deposit.Item != null)
                    {
                        missing = Take(Holding(deposit.Player, deposit.Item.Index), deposit.Quantity, preferDied: false,
                            (lot, n) => lot.Deposits.Add((deposit, n)));
                    }

                    if (missing > 0)
                    {
                        extras.Add((deposit, missing, deposit.UtcTime < firstLootUtc ? LootStatus.BeforeLog : LootStatus.DepositedWithoutLoot));
                    }

                    break;
                }
            }
        }

        // Второй проход: сдачи без своего лута ищут не сданный лут другого игрока той же гильдии.
        var depositsForOthers = new List<(ChestLogEntry Deposit, int Quantity, string Owner)>();
        for (var i = 0; i < extras.Count; i++)
        {
            var (deposit, missing, status) = extras[i];
            if (status != LootStatus.DepositedWithoutLoot || deposit.Item == null)
            {
                continue;
            }

            var depositorGuild = guilds.GetValueOrDefault(deposit.Player, string.Empty);
            var candidates = lots
                .Where(x => x.Remaining > 0
                            && x.Entry.ItemIndex == deposit.Item.Index
                            && x.Entry.UtcTime < deposit.UtcTime
                            && !string.Equals(x.Entry.LootedBy, deposit.Player, StringComparison.OrdinalIgnoreCase)
                            && SameGuildOrUnknown(depositorGuild, guilds.GetValueOrDefault(x.Entry.LootedBy, string.Empty)))
                .OrderByDescending(x => x.Entry.UtcTime)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            // берём лут одного игрока — того, чей подходящий лут самый свежий
            var owner = candidates[0].Entry.LootedBy;
            var ownerLots = candidates.Where(x => string.Equals(x.Entry.LootedBy, owner, StringComparison.OrdinalIgnoreCase)).ToList();
            var left = Take(ownerLots, missing, preferDied: false, (lot, n) => lot.OtherDeposits.Add((deposit, n, deposit.Player)));
            if (left < missing)
            {
                depositsForOthers.Add((deposit, missing - left, owner));
            }

            extras[i] = (deposit, left, status);
        }

        foreach (var lot in lots)
        {
            var player = GetPlayer(lot.Entry.LootedBy);
            foreach (var (deposit, quantity) in lot.Deposits)
            {
                player.Items.Add(new ComparisonItem { Player = player.Player, Loot = lot.Entry, Deposit = deposit, Quantity = quantity, Status = LootStatus.Deposited });
            }

            foreach (var (deposit, quantity, depositor) in lot.OtherDeposits)
            {
                player.Items.Add(new ComparisonItem
                {
                    Player = player.Player, Loot = lot.Entry, Deposit = deposit, Quantity = quantity,
                    Status = LootStatus.DepositedByOther, OtherPlayer = depositor
                });
            }

            if (lot.Lost > 0)
            {
                player.Items.Add(new ComparisonItem { Player = player.Player, Loot = lot.Entry, Quantity = lot.Lost, Status = LootStatus.Lost });
            }

            if (lot.Remaining > 0)
            {
                player.Items.Add(new ComparisonItem
                {
                    Player = player.Player,
                    Loot = lot.Entry,
                    Quantity = lot.Remaining,
                    Status = lot.Died ? LootStatus.DiedWithLoot : LootStatus.NotDeposited
                });
            }
        }

        foreach (var (deposit, quantity, status) in extras.Where(x => x.Quantity > 0))
        {
            GetPlayer(deposit.Player).Items.Add(new ComparisonItem { Player = deposit.Player, Deposit = deposit, Quantity = quantity, Status = status });
        }

        foreach (var (deposit, quantity, owner) in depositsForOthers)
        {
            GetPlayer(deposit.Player).Items.Add(new ComparisonItem
            {
                Player = deposit.Player, Deposit = deposit, Quantity = quantity,
                Status = LootStatus.DepositedForOther, OtherPlayer = owner
            });
        }

        foreach (var player in players.Values.Where(p => string.IsNullOrEmpty(p.Guild)))
        {
            player.Guild = guilds.GetValueOrDefault(player.Player, string.Empty);
        }

        return players.Values
            .OrderByDescending(x => x.NotDepositedValue)
            .ThenByDescending(x => x.NotDepositedCount)
            .ThenBy(x => x.Player, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Забирает штуки с рук игрока: сначала самые свежие партии
    /// (а при луте с тела — сначала то, с чем он погиб). Возвращает, сколько штук не нашлось.
    /// </summary>
    private static int Take(List<Lot> lots, int quantity, bool preferDied, Action<Lot, int> apply)
    {
        var order = lots
            .Where(x => x.Remaining > 0)
            .OrderByDescending(x => preferDied && x.Died)
            .ThenByDescending(x => x.Entry.UtcTime);

        foreach (var lot in order)
        {
            if (quantity <= 0)
            {
                break;
            }

            var taken = Math.Min(quantity, lot.Remaining);
            apply(lot, taken);
            quantity -= taken;
        }

        return quantity;
    }

    /// <summary>Гильдии игроков: из журнала (кто лутал и у кого лутали) и из переданного списка.</summary>
    private static Dictionary<string, string> CollectGuilds(IEnumerable<LootEntry> loot, IReadOnlyDictionary<string, string>? known)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in loot)
        {
            if (!string.IsNullOrEmpty(entry.LootedByGuild) && !string.IsNullOrEmpty(entry.LootedBy))
            {
                result.TryAdd(entry.LootedBy, entry.LootedByGuild);
            }

            if (!entry.IsFromMob && !string.IsNullOrEmpty(entry.LootedFromGuild) && !string.IsNullOrEmpty(entry.LootedFrom))
            {
                result.TryAdd(entry.LootedFrom, entry.LootedFromGuild);
            }
        }

        foreach (var (player, guild) in known ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrEmpty(guild))
            {
                result.TryAdd(player, guild);
            }
        }

        return result;
    }

    /// <summary>Разные гильдии — только если обе известны и не совпадают.</summary>
    private static bool SameGuildOrUnknown(string a, string b)
    {
        return string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Lot
    {
        public Lot(LootEntry entry)
        {
            Entry = entry;
        }

        public LootEntry Entry { get; }
        public List<(ChestLogEntry Deposit, int Quantity)> Deposits { get; } = new();
        public List<(ChestLogEntry Deposit, int Quantity, string Depositor)> OtherDeposits { get; } = new();
        public int Lost { get; set; }
        public bool Died { get; set; }
        public int Remaining => Entry.Quantity - Lost - Deposits.Sum(x => x.Quantity) - OtherDeposits.Sum(x => x.Quantity);
    }
}
