using System.Buffers.Binary;

namespace LootLogger.Core.Capture;

public enum PhotonProbeResult
{
    NotPhoton,

    /// <summary>Похоже на Photon, но направление пока неизвестно (например, только подтверждения).</summary>
    Photon,

    /// <summary>Пакет от сервера игры (события и ответы).</summary>
    FromServer,

    /// <summary>Пакет от клиента (запросы).</summary>
    FromClient
}

/// <summary>
/// Узнаёт пакеты Photon по содержимому, а не по порту. Нужно для ускорителей и VPN (GearUP, ExitLag и т. п.),
/// которые пускают трафик игры через свой локальный прокси на других портах.
/// Проверка строгая: заголовок, типы и длины команд должны точно совпасть с размером пакета.
/// </summary>
public static class PhotonProbe
{
    private const int HeaderLength = 12;

    /// <summary>Сколько байт служебного заголовка прокси (SOCKS5 и т. п.) допускаем перед пакетом игры.</summary>
    public const int MaxPrefixLength = 64;

    /// <summary>
    /// Ищет пакет Photon внутри датаграммы: сначала по известному сдвигу, затем со сдвигом 0…<see cref="MaxPrefixLength"/>.
    /// Ускорители часто добавляют перед пакетом игры свой заголовок (адрес и порт настоящего сервера).
    /// </summary>
    public static PhotonProbeResult Find(ReadOnlySpan<byte> datagram, int preferredOffset, out int offset)
    {
        if (preferredOffset >= 0 && preferredOffset < datagram.Length)
        {
            var preferred = Inspect(datagram[preferredOffset..]);
            if (preferred != PhotonProbeResult.NotPhoton)
            {
                offset = preferredOffset;
                return preferred;
            }
        }

        var last = Math.Min(MaxPrefixLength, datagram.Length - HeaderLength - CommandHeaderLength);
        for (var candidate = 0; candidate <= last; candidate++)
        {
            if (candidate == preferredOffset)
            {
                continue;
            }

            var result = Inspect(datagram[candidate..]);
            if (result != PhotonProbeResult.NotPhoton)
            {
                offset = candidate;
                return result;
            }
        }

        offset = -1;
        return PhotonProbeResult.NotPhoton;
    }
    private const int CommandHeaderLength = 12;

    public static PhotonProbeResult Inspect(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength + CommandHeaderLength)
        {
            return PhotonProbeResult.NotPhoton;
        }

        var flags = packet[2];
        if (flags != 0 && flags != 0xCC)
        {
            return PhotonProbeResult.NotPhoton;
        }

        int commandCount = packet[3];
        if (commandCount == 0)
        {
            return PhotonProbeResult.NotPhoton;
        }

        var offset = HeaderLength + (flags == 0xCC ? 4 : 0);
        var result = PhotonProbeResult.Photon;

        for (var i = 0; i < commandCount; i++)
        {
            if (offset + CommandHeaderLength > packet.Length)
            {
                return PhotonProbeResult.NotPhoton;
            }

            var type = packet[offset];
            if (type is < 1 or > 12)
            {
                return PhotonProbeResult.NotPhoton;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset + 4, 4));
            if (length < CommandHeaderLength || length > packet.Length - offset)
            {
                return PhotonProbeResult.NotPhoton;
            }

            if (type is 6 or 7)
            {
                var messageStart = offset + CommandHeaderLength + (type == 7 ? 4 : 0);
                if (messageStart + 2 <= offset + length && packet[messageStart] == 0xF3)
                {
                    result = packet[messageStart + 1] switch
                    {
                        2 => PhotonProbeResult.FromClient,
                        3 or 4 => PhotonProbeResult.FromServer,
                        _ => result
                    };
                }
            }

            offset += length;
        }

        return offset == packet.Length ? result : PhotonProbeResult.NotPhoton;
    }
}
