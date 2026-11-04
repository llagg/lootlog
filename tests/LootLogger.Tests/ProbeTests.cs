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
