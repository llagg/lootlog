using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using LootLogger.Core.Protocol;
using Xunit;
using static LootLogger.Tests.PhotonBuilder;

namespace LootLogger.Tests;

public class TrackerTests
{
    private static readonly Guid ContainerGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid InteractGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private sealed class Harness
    {
        public Harness()
        {
            var items = new ItemDb(
            [
                new ItemInfo(1, "T1_TRASH", "Мусор", "Trash"),
                new ItemInfo(2, "T4_MAIN_SWORD@1", "Меч адепта", "Adept's Broadsword"),
                new ItemInfo(3, "T5_POTION_HEAL", "Зелье лечения", "Healing Potion"),
                new ItemInfo(4, "T6_BAG", "Сумка мастера", "Master's Bag")
            ]);
            var clusters = new Dictionary<string, string> { ["0000"] = "Тестовая зона" };
            Prices = new PriceBook();
            Tracker = new LootTracker(new GameDatabase(items, clusters), Prices, () => Now);
            Tracker.LootRecorded += Loot.Add;
            Tracker.KillRecorded += Kills.Add;
            Incoming = new AlbionPacketParser(Tracker);
            Outgoing = new AlbionPacketParser(Tracker);
        }

        public DateTime Now { get; set; } = new(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc);
        public PriceBook Prices { get; }
        public LootTracker Tracker { get; }
        public AlbionPacketParser Incoming { get; }
        public AlbionPacketParser Outgoing { get; }
        public List<LootEntry> Loot { get; } = new();
        public List<KillEntry> Kills { get; } = new();
        private int _sequence = 1;

        public void Event(EventCodes code, Dictionary<byte, object> p) =>
            Incoming.ReceivePacket(EventPacket(_sequence++, (short) code, p));

        public void Request(OperationCodes code, Dictionary<byte, object> p) =>
            Outgoing.ReceivePacket(RequestPacket(_sequence++, (short) code, p));

        public void Response(OperationCodes code, Dictionary<byte, object> p) =>
            Incoming.ReceivePacket(ResponsePacket(_sequence++, (short) code, p));

        public void Join(string name = "Me", string guild = "MyGuild")
        {
            Response(OperationCodes.Join, new()
            {
                [0] = 42L,
                [2] = name,
                [8] = "0000",
                [54] = InteractGuid.ToByteArray(),
                [58] = guild
            });
        }

        public void Grab(string from, string by, int item, int quantity, bool silver = false)
        {
            var p = new Dictionary<byte, object> { [0] = 1L, [1] = from, [2] = by, [4] = item, [5] = quantity };
            if (silver)
            {
                p[3] = true;
            }

            Event(EventCodes.OtherGrabbedLoot, p);
        }
    }

    [Fact]
    public void OtherPlayerLoot_IsRecordedWithGuildAndZone()
    {
        var h = new Harness();
        h.Join();
        h.Event(EventCodes.NewCharacter, new() { [0] = 10L, [1] = "Looter", [8] = "Robbers", [51] = "ALLY" });

        h.Grab("Victim", "Looter", 2, 1);

        var entry = Assert.Single(h.Loot);
        Assert.Equal("Looter", entry.LootedBy);
        Assert.Equal("Robbers", entry.LootedByGuild);
        Assert.Equal("ALLY", entry.LootedByAlliance);
        Assert.Equal("Victim", entry.LootedFrom);
        Assert.Equal("Меч адепта (T4.1)", entry.ItemName);
        Assert.Equal("T4_MAIN_SWORD@1", entry.UniqueName);
        Assert.Equal("Тестовая зона", entry.Cluster);
        Assert.False(entry.IsFromMob);
        Assert.False(entry.IsPartyRelated);
    }

    [Fact]
    public void SilverAndTrash_AreIgnored()
    {
        var h = new Harness();
        h.Grab("Victim", "Looter", 0, 15000, silver: true);
        h.Grab("Victim", "Looter", 1, 1);
        Assert.Empty(h.Loot);
    }

    [Fact]
    public void MobLoot_IsNormalized()
    {
        var h = new Harness();
        h.Grab("@MOB_FOREST_GIANT", "Looter", 3, 2);
        var entry = Assert.Single(h.Loot);
        Assert.Equal(LootTracker.MobName, entry.LootedFrom);
        Assert.True(entry.IsFromMob);
    }

    [Fact]
    public void RetransmittedReliableCommand_IsCountedOnce()
    {
        var h = new Harness();
        var packet = EventPacket(500, (short) EventCodes.OtherGrabbedLoot,
            new() { [1] = "Victim", [2] = "Looter", [4] = 2, [5] = 1 });

        h.Incoming.ReceivePacket(packet);
        h.Incoming.ReceivePacket(packet); // повтор той же команды

        Assert.Single(h.Loot);

        // та же добыча, но новая команда — это отдельный лут
        h.Incoming.ReceivePacket(EventPacket(501, (short) EventCodes.OtherGrabbedLoot,
            new() { [1] = "Victim", [2] = "Looter", [4] = 2, [5] = 1 }));
        Assert.Equal(2, h.Loot.Count);
    }

    [Fact]
    public void FragmentedEvent_IsReassembled()
    {
        var h = new Harness();
        var body = MessageBody(4, PhotonBuilder.Event((short) EventCodes.OtherGrabbedLoot,
            new() { [1] = "Victim", [2] = "Looter", [4] = 4, [5] = 1 }));
        var half = body.Length / 2;

        h.Incoming.ReceivePacket(Packet(1, 9, Fragment(10, 10, 2, 0, body.Length, 0, body[..half])));
        Assert.Empty(h.Loot);
        h.Incoming.ReceivePacket(Packet(1, 9, Fragment(11, 10, 2, 1, body.Length, half, body[half..])));

        var entry = Assert.Single(h.Loot);
        Assert.Equal(4, entry.ItemIndex);
    }

    [Fact]
    public void Value_ComesFromGameEstimate()
    {
        var h = new Harness();
        // сервер прислал предмет с оценкой 1 234 серебра (внутреннее значение ×10000)
        h.Event(EventCodes.NewSimpleItem, new() { [0] = 900L, [1] = 3, [2] = 10, [4] = 12_340_000L });

        h.Grab("Victim", "Looter", 3, 5);

        var entry = Assert.Single(h.Loot);
        Assert.Equal(1234, entry.UnitValue);
        Assert.Equal(6170, entry.TotalValue);
    }

    [Fact]
    public void LocalLoot_FromContainer_IsRecordedOnce()
    {
        var h = new Harness();
        h.Join("Me");
        h.Event(EventCodes.NewLoot, new() { [0] = 100L, [3] = "Enemy" });
        h.Event(EventCodes.NewEquipmentItem, new() { [0] = 555L, [1] = 2, [2] = 1, [4] = 50_000_000L, [6] = 3 });
        h.Event(EventCodes.AttachItemContainer, new() { [0] = 100L, [1] = ContainerGuid.ToByteArray(), [3] = new long[] { 0, 555 } });

        h.Request(OperationCodes.InventoryMoveItem, new() { [0] = 1, [1] = ContainerGuid.ToByteArray(), [4] = InteractGuid.ToByteArray() });

        var entry = Assert.Single(h.Loot);
        Assert.Equal("Me", entry.LootedBy);
        Assert.Equal("Enemy", entry.LootedFrom);
        Assert.Equal(5000, entry.UnitValue);
        Assert.True(entry.IsPartyRelated);

        // игра может прислать тот же лут ещё и общим событием — второй раз не записываем
        h.Grab("Enemy", "Me", 2, 1);
        Assert.Single(h.Loot);

        // повторный запрос перемещения того же предмета тоже не записываем
        h.Request(OperationCodes.InventoryMoveItem, new() { [0] = 1, [1] = ContainerGuid.ToByteArray(), [4] = InteractGuid.ToByteArray() });
        Assert.Single(h.Loot);
    }

    [Fact]
    public void LocalLoot_LootAllRequest_RecordsEveryItem()
    {
        var h = new Harness();
        h.Join("Me");
        h.Event(EventCodes.NewLoot, new() { [0] = 100L, [3] = "@MOB_KEEPER" });
        h.Event(EventCodes.NewSimpleItem, new() { [0] = 601L, [1] = 3, [2] = 4, [4] = 10_000L });
        h.Event(EventCodes.NewSimpleItem, new() { [0] = 602L, [1] = 4, [2] = 1, [4] = 20_000L });
        h.Event(EventCodes.AttachItemContainer, new() { [0] = 100L, [1] = ContainerGuid.ToByteArray(), [3] = new long[] { 601, 602 } });

        h.Request(OperationCodes.InventoryMoveGivenItems, new()
        {
            [0] = ContainerGuid.ToByteArray(),
            [2] = InteractGuid.ToByteArray(),
            [4] = new long[] { 601, 602 }
        });

        Assert.Equal(2, h.Loot.Count);
        Assert.All(h.Loot, e => Assert.Equal(LootTracker.MobName, e.LootedFrom));
    }

    [Fact]
    public void LocalLoot_IsIgnored_ForSomeoneElsesInteraction()
    {
        var h = new Harness();
        h.Join("Me");
        h.Event(EventCodes.NewLoot, new() { [0] = 100L, [3] = "Enemy" });
        h.Event(EventCodes.NewSimpleItem, new() { [0] = 601L, [1] = 3, [2] = 4, [4] = 10_000L });
        h.Event(EventCodes.AttachItemContainer, new() { [0] = 100L, [1] = ContainerGuid.ToByteArray(), [3] = new long[] { 601 } });

        h.Request(OperationCodes.InventoryMoveItem, new() { [0] = 0, [1] = ContainerGuid.ToByteArray(), [4] = Guid.NewGuid().ToByteArray() });

        Assert.Empty(h.Loot);
    }

    [Fact]
    public void Party_MarksLootAsPartyRelated()
    {
        var h = new Harness();
        h.Join("Me");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        h.Event(EventCodes.PartyJoined, new()
        {
            [8] = a.ToByteArray().Concat(b.ToByteArray()).ToArray(),
            [9] = new[] { "Me", "Friend" }
        });

        h.Grab("Victim", "Friend", 2, 1);
        h.Grab("Friend", "Enemy", 3, 1);
        h.Grab("Victim", "Stranger", 4, 1);

        Assert.True(h.Loot[0].IsPartyRelated);
        Assert.True(h.Loot[1].IsPartyRelated); // лутают нашего
        Assert.False(h.Loot[2].IsPartyRelated);

        h.Event(EventCodes.PartyPlayerLeft, new() { [1] = b.ToByteArray() });
        h.Grab("Victim", "Friend", 2, 1);
        Assert.False(h.Loot[3].IsPartyRelated);
        Assert.Equal(1, h.Tracker.PartySize);
    }

    [Fact]
    public void Death_IsRecorded()
    {
        var h = new Harness();
        h.Event(EventCodes.Died, new() { [1] = 5L, [2] = "Victim", [3] = "VGuild", [9] = 6L, [10] = "Killer", [11] = "KGuild" });
        var kill = Assert.Single(h.Kills);
        Assert.Equal("Victim", kill.Died);
        Assert.Equal("Killer", kill.KilledBy);
    }

    [Fact]
    public void ChangeCluster_UpdatesZone()
    {
        var h = new Harness();
        h.Join();
        h.Response(OperationCodes.ChangeCluster, new() { [0] = "0000@RANDOMDUNGEON" });
        Assert.Equal("Тестовая зона (инстанс)", h.Tracker.CurrentCluster);
    }

    [Fact]
    public void UnrelatedEvents_AreSkippedSafely()
    {
        var h = new Harness();
        h.Event(EventCodes.Move, new() { [0] = 1L, [1] = new byte[] { 1, 2, 3 } });
        h.Incoming.ReceivePacket(new byte[] { 1, 2, 3 });
        h.Incoming.ReceivePacket(new byte[40]);
        Assert.Empty(h.Loot);
        Assert.Equal(0, h.Incoming.HandlerErrors);
    }
}
