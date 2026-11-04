using LootLogger.Core.Capture;
using LootLogger.Core.Protocol;
using Xunit;
using static LootLogger.Tests.PhotonBuilder;

namespace LootLogger.Tests;

public class ProbeTests
{
    [Fact]
    public void ServerEvent_IsRecognized()
    {
        var packet = EventPacket(1, (short) EventCodes.OtherGrabbedLoot, new() { [1] = "A", [2] = "B", [4] = 1, [5] = 1 });
        Assert.Equal(PhotonProbeResult.FromServer, PhotonProbe.Inspect(packet));
    }

    [Fact]
    public void ClientRequest_IsRecognized()
    {
        var packet = RequestPacket(1, (short) OperationCodes.InventoryMoveItem, new() { [0] = 1 });
        Assert.Equal(PhotonProbeResult.FromClient, PhotonProbe.Inspect(packet));
    }

    [Fact]
    public void Response_IsFromServer()
    {
        var packet = ResponsePacket(1, (short) OperationCodes.Join, new() { [2] = "Me" });
        Assert.Equal(PhotonProbeResult.FromServer, PhotonProbe.Inspect(packet));
    }

    [Fact]
    public void Fragment_IsPhotonWithoutDirection()
    {
        var packet = Packet(1, 2, Fragment(5, 5, 2, 0, 100, 0, new byte[50]));
        Assert.Equal(PhotonProbeResult.Photon, PhotonProbe.Inspect(packet));
    }

    [Fact]
    public void RandomData_IsRejected()
    {
        var random = new Random(42);
        var rejected = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var data = new byte[random.Next(12, 1400)];
            random.NextBytes(data);
            if (PhotonProbe.Inspect(data) == PhotonProbeResult.NotPhoton)
            {
                rejected++;
            }
        }

        Assert.True(rejected >= 9_990, $"rejected {rejected}");
    }

    [Fact]
    public void TruncatedPacket_IsRejected()
    {
        var packet = EventPacket(1, (short) EventCodes.OtherGrabbedLoot, new() { [1] = "A", [2] = "B", [4] = 1, [5] = 1 });
        Assert.Equal(PhotonProbeResult.NotPhoton, PhotonProbe.Inspect(packet[..^3]));
    }
}

public class ProbePrefixTests
{
    [Fact]
    public void GamePacket_BehindSocks5Header_IsFound()
    {
        var game = PhotonBuilder.EventPacket(1, (short) LootLogger.Core.Protocol.EventCodes.OtherGrabbedLoot,
            new() { [1] = "A", [2] = "B", [4] = 1, [5] = 1 });
        // SOCKS5 UDP: RSV RSV FRAG ATYP=IPv4 ADDR(4) PORT(2)
        var socks = new byte[] { 0, 0, 0, 1, 5, 188, 125, 10, 0x13, 0xC0 };
        var datagram = socks.Concat(game).ToArray();

        Assert.Equal(PhotonProbeResult.NotPhoton, PhotonProbe.Inspect(datagram));
        Assert.Equal(PhotonProbeResult.FromServer, PhotonProbe.Find(datagram, -1, out var offset));
        Assert.Equal(10, offset);

        // известный сдвиг проверяется первым
        Assert.Equal(PhotonProbeResult.FromServer, PhotonProbe.Find(datagram, 10, out offset));
        Assert.Equal(10, offset);
    }

    [Fact]
    public void Find_RejectsRandomData()
    {
        var random = new Random(7);
        var found = 0;
        for (var i = 0; i < 3_000; i++)
        {
            var data = new byte[random.Next(12, 1400)];
            random.NextBytes(data);
            if (PhotonProbe.Find(data, -1, out _) != PhotonProbeResult.NotPhoton)
            {
                found++;
            }
        }

        Assert.True(found <= 3, $"false positives: {found}");
    }
}
