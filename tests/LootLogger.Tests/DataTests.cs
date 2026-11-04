using System.Buffers.Binary;
using System.Text;
using LootLogger.Core.Capture;
using LootLogger.Core.Comparator;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using LootLogger.Core.Storage;
using Xunit;

namespace LootLogger.Tests;

public class GameDataTests
{
    [Fact]
    public void ItemIndices_FollowGameOrder()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <items>
              <shopcategories><shopcategory id="x" /></shopcategories>
              <simpleitem uniquename="T1_TRASH" />
              <weapon uniquename="T4_MAIN_SWORD">
                <enchantments>
                  <enchantment enchantmentlevel="1" />
                  <enchantment enchantmentlevel="2" />
                </enchantments>
              </weapon>
              <simpleitem uniquename="T4_ARTEFACT_SWORD" descvariable0="@ITEMS_OTHER" />
              <journalitem uniquename="T4_JOURNAL_MAGE" />
              <simpleitem uniquename="T5_BAG" enchantmentlevel="1" />
            </items>
            """;
        var names = new Dictionary<string, (string Ru, string En)>(StringComparer.OrdinalIgnoreCase)
        {
            ["@ITEMS_T4_MAIN_SWORD"] = ("Меч адепта", "Adept's Broadsword"),
            ["@ITEMS_T4_ARTEFACT_SWORD"] = ("Артефакт", "Artefact"),
            ["@ITEMS_T4_JOURNAL_MAGE_FULL"] = ("Полный журнал", "Full journal")
        };

        var items = GameDataLoader.ParseItemsXml(Encoding.UTF8.GetBytes(xml), names);

        Assert.Equal(
            new[] { "T1_TRASH", "T4_MAIN_SWORD", "T4_MAIN_SWORD@1", "T4_MAIN_SWORD@2", "T4_ARTEFACT_SWORD", "T4_JOURNAL_MAGE", "T5_BAG@1", "T4_JOURNAL_MAGE_EMPTY", "T4_JOURNAL_MAGE_FULL" },
            items.Select(x => x.UniqueName).ToArray());
        Assert.Equal(Enumerable.Range(1, 9), items.Select(x => x.Index));
        Assert.Equal("Меч адепта", items[2].NameRu);
        Assert.Equal("Артефакт", items[4].NameRu);
        Assert.Equal("Полный журнал", items[8].NameRu);

        var db = new ItemDb(items);
        Assert.Equal("Меч адепта (T4.2)", db.Get(4)!.DisplayName);
        Assert.Equal(3, db.FindByLocalizedName("меч адепта", 1)!.Index);
        Assert.Equal(3, db.FindByLocalizedName("Adept's Broadsword", 1)!.Index);
        Assert.True(db.Get(1)!.IsTrash);
        Assert.Null(db.Get(0));
        Assert.Null(db.Get(1000));
    }

    [Fact]
    public void ClusterNames_ForInstances()
    {
        var game = new GameDatabase(ItemDb.Empty, new Dictionary<string, string> { ["4000"] = "Martlock" });
        Assert.Equal("Martlock", game.ClusterDisplayName("4000"));
        Assert.Equal("Martlock (инстанс)", game.ClusterDisplayName("4000@HIDEOUT@X"));
        Assert.Equal("Martlock (Остров)", game.ClusterDisplayName("4000", "Остров"));
        Assert.Equal("9999", game.ClusterDisplayName("9999"));
    }

    [Fact]
    public void ResolveGameDirectory_AcceptsRootOrGameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ll-test-" + Guid.NewGuid().ToString("N"));
        var gameData = Path.Combine(root, "game", "Albion-Online_Data", "StreamingAssets", "GameData");
        Directory.CreateDirectory(gameData);
        File.WriteAllBytes(Path.Combine(gameData, "items.bin"), [1]);
        try
        {
            var expected = Path.Combine(root, "game");
            Assert.Equal(expected, GameDataLoader.ResolveGameDirectory(root));
            Assert.Equal(expected, GameDataLoader.ResolveGameDirectory(expected));
            Assert.Equal(expected, GameDataLoader.ResolveGameDirectory(gameData));
            Assert.Null(GameDataLoader.ResolveGameDirectory(Path.GetTempPath() + "nope-" + Guid.NewGuid()));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class PriceTests
{
    [Fact]
    public void NearestToAverage_TrimsExtremes()
    {
        Assert.Equal(0, PriceBook.NearestToAverage([]));
        Assert.Equal(100, PriceBook.NearestToAverage([100]));
        Assert.Equal(100, PriceBook.NearestToAverage([300, 100]));
        Assert.Equal(200, PriceBook.NearestToAverage([1, 200, 210, 1_000_000]));
    }

    [Fact]
    public void Prices_SurviveRestart_ByUniqueName()
    {
        var items = new ItemDb([new ItemInfo(1, "T4_BAG", "Сумка", "Bag")]);
        var file = Path.GetTempFileName();
        try
        {
            var book = new PriceBook();
            book.Record(1, 2, 25_000_000);
            book.Save(file, items);

            var restored = new PriceBook();
            restored.Load(file, items);
            Assert.Equal(2500, restored.GetUnitValue(1));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class CsvTests
{
    private static readonly ItemDb Items = new(
    [
        new ItemInfo(1, "T4_MAIN_SWORD@1", "Меч адепта", "Adept's Broadsword"),
        new ItemInfo(2, "T5_POTION_HEAL", "Зелье; лечения", "Healing Potion")
    ]);

    [Fact]
    public void Csv_RoundTrip()
    {
        var entries = new[]
        {
            new LootEntry
            {
                UtcTime = new DateTime(2026, 11, 4, 18, 30, 15, DateTimeKind.Utc),
                LootedBy = "Looter", LootedByGuild = "Guild A", LootedByAlliance = "ALLY",
                LootedFrom = "Victim", LootedFromGuild = "Guild B",
                ItemIndex = 1, UniqueName = "T4_MAIN_SWORD@1", ItemName = "Меч адепта (T4.1)",
                Quantity = 1, UnitValue = 12345, Cluster = "Martlock", IsPartyRelated = true
            },
            new LootEntry
            {
                UtcTime = new DateTime(2026, 11, 4, 18, 31, 0, DateTimeKind.Utc),
                LootedBy = "Looter", LootedFrom = LootTracker.MobName, IsFromMob = true,
                ItemIndex = 2, UniqueName = "T5_POTION_HEAL", ItemName = "Зелье; лечения", Quantity = 5
            }
        };

        var file = Path.GetTempFileName();
        try
        {
            LootCsv.Write(file, entries);
            var result = LootCsv.Read(file, Items);

            Assert.True(result.IsValid);
            Assert.Equal(2, result.Loot.Count);
            var a = result.Loot[0];
            Assert.Equal(entries[0].UtcTime, a.UtcTime);
            Assert.Equal("Looter", a.LootedBy);
            Assert.Equal("Guild A", a.LootedByGuild);
            Assert.Equal("ALLY", a.LootedByAlliance);
            Assert.Equal("Victim", a.LootedFrom);
            Assert.Equal(1, a.ItemIndex);
            Assert.Equal(12345, a.UnitValue);
            Assert.True(a.IsPartyRelated);
            Assert.Equal("Martlock", a.Cluster);

            var b = result.Loot[1];
            Assert.True(b.IsFromMob);
            Assert.Equal(5, b.Quantity);
            Assert.Equal(2, b.ItemIndex);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Reads_StatisticsAnalysisTool_Export()
    {
        var sat = new StringBuilder();
        sat.AppendLine("timestamp_utc;looted_by__alliance;looted_by__guild;looted_by__name;item_id;item_name;quantity;looted_from__alliance;looted_from__guild;looted_from__name;died;died_player_guild;killed_by;killed_by_guild;average_est_market_value;cluster;died_player_alliance;killed_by_alliance");
        sat.AppendLine("2026-11-04T18:00:00.0000000Z;;G;Looter;T4_MAIN_SWORD@1;Adept's Broadsword;1;;;MOB;;;;;5000;Martlock;;");
        sat.AppendLine("2026-11-04T18:01:00.0000000Z;;;;;;;;;;Victim;VG;Killer;KG;;Martlock;;");

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, sat.ToString(), new UTF8Encoding(true));
            var result = LootCsv.Read(file, Items);
            Assert.True(result.IsValid);
            var loot = Assert.Single(result.Loot);
            Assert.True(loot.IsFromMob);
            Assert.Equal(5000, loot.UnitValue);
            var kill = Assert.Single(result.Kills);
            Assert.Equal("Killer", kill.KilledBy);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void NotALootLog_IsRejected()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "a;b;c\n1;2;3\n");
            Assert.False(LootCsv.Read(file, Items).IsValid);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AutoSave_WritesHeaderAndRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ll-autosave-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new AutoSaveWriter(dir))
            {
                writer.Add(new LootEntry { UtcTime = DateTime.UtcNow, LootedBy = "A", LootedFrom = "B", ItemIndex = 1, Quantity = 1, UniqueName = "T4_MAIN_SWORD@1" });
                writer.Add(new KillEntry { UtcTime = DateTime.UtcNow, Died = "B", KilledBy = "A" });
            }

            var file = Assert.Single(Directory.GetFiles(dir, "loot_*.csv"));
            var lines = File.ReadAllLines(file);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("timestamp_utc;", lines[0].TrimStart('﻿'));

            var read = LootCsv.Read(file, Items);
            Assert.Single(read.Loot);
            Assert.Single(read.Kills);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}

public class ComparatorTests
{
    private static readonly ItemDb Items = new(
    [
        new ItemInfo(1, "T4_MAIN_SWORD@1", "Меч адепта", "Adept's Broadsword"),
        new ItemInfo(2, "T6_BAG", "Сумка мастера", "Master's Bag"),
        new ItemInfo(3, "T5_2H_HAMMER", "Молот эксперта", "Expert's Great Hammer"),
        new ItemInfo(4, "T5_POTION_HEAL", "Зелье лечения", "Healing Potion")
    ]);

    private static DateTime T(int hour, int minute) => new(2026, 11, 4, hour, minute, 0, DateTimeKind.Utc);

    private static LootEntry Loot(DateTime time, string by, string from, int item, int qty, long value = 1000) => new()
    {
        UtcTime = time, LootedBy = by, LootedFrom = from, ItemIndex = item, Quantity = qty, UnitValue = value,
        ItemName = Items.DisplayName(item), IsFromMob = from == LootTracker.MobName
    };

    [Fact]
    public void ChestLog_IsParsed_RussianAndEnglish()
    {
        var text = "Дата\tИгрок\tПредмет\tЗачарование\tКачество\tКоличество\n" +
                   "04.11.2026 18:15:00\tLooter\tМеч адепта\t1\t2\t1\n" +
                   "\"11/04/2026 18:16:00\"\t\"Looter\"\t\"Master's Bag\"\t\"0\"\t\"1\"\t\"1\"\n" +
                   "2026-11-04 18:17:00\tOther\tНеизвестная вещь\t0\t1\t3\n" +
                   "2026-11-04 18:18:00\tOther\tЗелье лечения\t0\t1\t-5\n";

        var result = ChestLogParser.Parse(text, Items);

        Assert.Equal(4, result.Entries.Count);
        Assert.Equal(1, result.SkippedLines);
        Assert.Equal(1, result.UnknownItems);
        Assert.Equal(1, result.Entries[0].Item!.Index);
        Assert.Equal(T(18, 15), result.Entries[0].UtcTime);
        Assert.Equal(2, result.Entries[1].Item!.Index);
        Assert.Null(result.Entries[2].Item);
    }

    [Fact]
    public void Comparison_ClassifiesEveryCase()
    {
        var loot = new[]
        {
            Loot(T(18, 0), "Looter", "Victim", 1, 1, 5000), // сдал
            Loot(T(18, 5), "Looter", "Victim", 2, 1, 7000), // не сдал
            Loot(T(18, 6), "Looter", "Victim", 3, 1, 9000), // потерял — с него залутали
            Loot(T(18, 10), "Enemy", "Looter", 3, 1, 9000), // враг залутал молот с Looter
            Loot(T(18, 30), "Looter", "Victim", 1, 1, 5000) // лут ПОСЛЕ сдачи — не должен закрыться ранней сдачей
        };

        var deposits = new[]
        {
            new ChestLogEntry { UtcTime = T(18, 15), Player = "looter", ItemName = "Меч адепта", Enchantment = 1, Quantity = 1, Item = Items.Get(1) },
            new ChestLogEntry { UtcTime = T(18, 20), Player = "Looter", ItemName = "Зелье лечения", Quantity = 10, Item = Items.Get(4) },
            new ChestLogEntry { UtcTime = T(17, 0), Player = "Other", ItemName = "Сумка мастера", Quantity = 1, Item = Items.Get(2) },
            new ChestLogEntry { UtcTime = T(18, 25), Player = "Looter", ItemName = "Сумка мастера", Quantity = -1, Item = Items.Get(2) } // забрал из сундука
        };

        var result = LootComparator.Compare(loot, deposits);

        var looter = result.Single(x => x.Player == "Looter");
        Assert.Equal(4, looter.LootedCount);
        Assert.Equal(1, looter.DepositedCount);
        Assert.Equal(2, looter.NotDepositedCount); // сумка и второй меч
        Assert.Equal(1, looter.LostCount);
        Assert.Equal(1, looter.ExtraDepositCount);
        Assert.Equal(12000, looter.NotDepositedValue);

        var deposited = looter.Items.Single(x => x.Status == LootStatus.Deposited);
        Assert.Equal(T(18, 0), deposited.LootUtc);
        Assert.Equal(T(18, 15), deposited.DepositUtc);

        var other = result.Single(x => x.Player == "Other");
        Assert.Equal(LootStatus.BeforeLog, Assert.Single(other.Items).Status);

        var enemy = result.Single(x => x.Player == "Enemy");
        Assert.Equal(1, enemy.NotDepositedCount);

        // первым в списке — тот, кто не сдал больше всего
        Assert.Equal("Looter", result[0].Player);
    }

    [Fact]
    public void Comparison_PrefersLatestLootBeforeDeposit()
    {
        var loot = new[]
        {
            Loot(T(18, 0), "A", "X", 1, 1),
            Loot(T(18, 10), "A", "Y", 1, 1)
        };
        var deposits = new[] { new ChestLogEntry { UtcTime = T(18, 20), Player = "A", ItemName = "x", Quantity = 1, Item = Items.Get(1) } };

        var a = Assert.Single(LootComparator.Compare(loot, deposits));
        Assert.Equal("Y", a.Items.Single(x => x.Status == LootStatus.Deposited).LootedFrom);
        Assert.Equal("X", a.Items.Single(x => x.Status == LootStatus.NotDeposited).LootedFrom);
    }
}

public class FrameTests
{
    private static byte[] Ipv4Udp(ushort srcPort, ushort dstPort, byte[] payload, ushort id = 1, int fragmentOffset = 0, bool more = false, int? udpLengthOverride = null)
    {
        var udp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort) (udpLengthOverride ?? udp.Length));
        payload.CopyTo(udp, 8);
        return Ipv4(udp, id, fragmentOffset, more);
    }

    private static byte[] Ipv4(byte[] data, ushort id, int fragmentOffset, bool more)
    {
        var ip = new byte[20 + data.Length];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort) ip.Length);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(6), (ushort) ((more ? 0x2000 : 0) | (fragmentOffset / 8)));
        ip[8] = 64;
        ip[9] = 17;
        new byte[] { 5, 188, 125, 10 }.CopyTo(ip, 12);
        new byte[] { 192, 168, 1, 2 }.CopyTo(ip, 16);
        data.CopyTo(ip, 20);
        return ip;
    }

    private static byte[] Ethernet(byte[] ip)
    {
        var frame = new byte[14 + ip.Length];
        frame[12] = 0x08;
        frame[13] = 0x00;
        ip.CopyTo(frame, 14);
        return frame;
    }

    [Fact]
    public void Ethernet_Ipv4_Udp_IsParsed()
    {
        var payload = Enumerable.Range(0, 30).Select(i => (byte) i).ToArray();
        var frame = Ethernet(Ipv4Udp(5056, 50000, payload));

        Assert.True(FrameParser.TryGetIpPacket(FrameParser.LinkEthernet, frame, out var ip));
        Assert.True(FrameParser.TryReadIpv4(ip, out var header));
        Assert.False(header.IsFragment);
        Assert.Equal(17, header.Protocol);
        Assert.True(FrameParser.TryReadUdp(ip.Slice(header.HeaderLength, header.TotalLength - header.HeaderLength), out var src, out var dst, out var udpPayload));
        Assert.Equal(5056, src);
        Assert.Equal(50000, dst);
        Assert.Equal(payload, udpPayload.ToArray());
    }

    [Fact]
    public void RawLink_IsSupported()
    {
        var ip = Ipv4Udp(50000, 5056, [1, 2, 3]);
        Assert.True(FrameParser.TryGetIpPacket(FrameParser.LinkRaw, ip, out var raw));
        Assert.Equal(ip.Length, raw.Length);

        var nullFrame = new byte[] { 2, 0, 0, 0 }.Concat(ip).ToArray();
        Assert.True(FrameParser.TryGetIpPacket(FrameParser.LinkNull, nullFrame, out var fromNull));
        Assert.Equal(ip.Length, fromNull.Length);
    }

    [Fact]
    public void Fragments_AreReassembled()
    {
        var payload = Enumerable.Range(0, 100).Select(i => (byte) i).ToArray();
        var udp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp, 5056);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), 40000);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort) udp.Length);
        payload.CopyTo(udp, 8);

        var first = Ipv4(udp[..56], 77, 0, more: true);
        var second = Ipv4(udp[56..], 77, 56, more: false);

        var reassembler = new Ipv4Reassembler();
        Assert.True(FrameParser.TryReadIpv4(second, out var h2));
        Assert.Null(reassembler.Add(second, h2)); // порядок прихода может быть любым
        Assert.True(FrameParser.TryReadIpv4(first, out var h1));
        var assembled = reassembler.Add(first, h1);

        Assert.NotNull(assembled);
        Assert.True(FrameParser.TryReadUdp(assembled, out var src, out _, out var result));
        Assert.Equal(5056, src);
        Assert.Equal(payload, result.ToArray());
    }
}
