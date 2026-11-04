using System.Buffers.Binary;

namespace LootLogger.Core.Capture;

/// <summary>Минимальный разбор кадров: Ethernet/RAW/NULL → IPv4/IPv6 → UDP.</summary>
public static class FrameParser
{
    public const int LinkNull = 0;
    public const int LinkEthernet = 1;
    public const int LinkRaw = 12;
    public const int LinkRawAlt = 14;
    public const int LinkRawLinktype = 101;
    public const int LinkIPv4 = 228;
    public const int LinkIPv6 = 229;

    /// <summary>Отрезает заголовок канального уровня и возвращает IP-пакет.</summary>
    public static bool TryGetIpPacket(int dataLink, ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> ip)
    {
        ip = default;
        switch (dataLink)
        {
            case LinkEthernet:
            {
                if (frame.Length < 14)
                {
                    return false;
                }

                var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
                var offset = 14;

                // VLAN-теги (802.1Q / QinQ)
                while (etherType is 0x8100 or 0x88A8 && frame.Length >= offset + 4)
                {
                    etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]);
                    offset += 4;
                }

                if (etherType is not (0x0800 or 0x86DD))
                {
                    return false;
                }

                ip = frame[offset..];
                return true;
            }
            case LinkNull:
            {
                // 4 байта семейства адресов в порядке байт хоста
                if (frame.Length < 4)
                {
                    return false;
                }

                ip = frame[4..];
                return ip.Length > 0;
            }
            case LinkRaw:
            case LinkRawAlt:
            case LinkRawLinktype:
            case LinkIPv4:
            case LinkIPv6:
                ip = frame;
                return ip.Length > 0;
            default:
                return false;
        }
    }

    public readonly ref struct Ipv4Header
    {
        public Ipv4Header(int headerLength, int totalLength, ushort identification, bool moreFragments, int fragmentOffset, byte protocol, uint source, uint destination)
        {
            HeaderLength = headerLength;
            TotalLength = totalLength;
            Identification = identification;
            MoreFragments = moreFragments;
            FragmentOffset = fragmentOffset;
            Protocol = protocol;
            Source = source;
            Destination = destination;
        }

        public int HeaderLength { get; }
        public int TotalLength { get; }
        public ushort Identification { get; }
        public bool MoreFragments { get; }
        public int FragmentOffset { get; }
        public byte Protocol { get; }
        public uint Source { get; }
        public uint Destination { get; }
        public bool IsFragment => MoreFragments || FragmentOffset != 0;
    }

    public static bool TryReadIpv4(ReadOnlySpan<byte> ip, out Ipv4Header header)
    {
        header = default;
        if (ip.Length < 20 || ip[0] >> 4 != 4)
        {
            return false;
        }

        var headerLength = (ip[0] & 0x0F) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (headerLength < 20 || totalLength < headerLength || ip.Length < headerLength)
        {
            return false;
        }

        // Некоторые драйверы отдают кадр с «хвостом» или с нулевой длиной (offload) — берём что есть.
        if (totalLength > ip.Length || totalLength == 0)
        {
            totalLength = (ushort) ip.Length;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(ip[6..]);
        header = new Ipv4Header(
            headerLength,
            totalLength,
            BinaryPrimitives.ReadUInt16BigEndian(ip[4..]),
            (flags & 0x2000) != 0,
            (flags & 0x1FFF) * 8,
            ip[9],
            BinaryPrimitives.ReadUInt32BigEndian(ip[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(ip[16..]));
        return true;
    }

    /// <summary>IPv6 без заголовков расширения (игра их не использует).</summary>
    public static bool TryGetIpv6Udp(ReadOnlySpan<byte> ip, out ReadOnlySpan<byte> udp)
    {
        udp = default;
        if (ip.Length < 40 || ip[0] >> 4 != 6 || ip[6] != 17)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(ip[4..]);
        var available = ip.Length - 40;
        udp = ip.Slice(40, payloadLength > 0 && payloadLength <= available ? payloadLength : available);
        return true;
    }

    public static bool TryReadUdp(ReadOnlySpan<byte> udp, out ushort sourcePort, out ushort destinationPort, out ReadOnlySpan<byte> payload)
    {
        sourcePort = destinationPort = 0;
        payload = default;
        if (udp.Length < 8)
        {
            return false;
        }

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(udp[4..]);
        var payloadLength = length >= 8 && length <= udp.Length ? length - 8 : udp.Length - 8;
        payload = udp.Slice(8, payloadLength);
        return true;
    }
}

/// <summary>Сборка фрагментированных IPv4-пакетов (бывает при большом MTU у VPN).</summary>
public sealed class Ipv4Reassembler
{
    private const int MaxPending = 128;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Dictionary<(uint Src, uint Dst, ushort Id, byte Proto), Buffer> _pending = new();

    /// <summary>Добавляет фрагмент. Возвращает полезную нагрузку IP, когда пакет собран целиком.</summary>
    public byte[]? Add(ReadOnlySpan<byte> ip, in FrameParser.Ipv4Header header)
    {
        var fragment = ip.Slice(header.HeaderLength, header.TotalLength - header.HeaderLength);
        if (fragment.Length == 0 || header.FragmentOffset + fragment.Length > 65535)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (_pending.Count >= MaxPending)
        {
            foreach (var expired in _pending.Where(x => now - x.Value.LastSeen > Timeout).Select(x => x.Key).ToList())
            {
                _pending.Remove(expired);
            }

            if (_pending.Count >= MaxPending)
            {
                _pending.Clear();
            }
        }

        var key = (header.Source, header.Destination, header.Identification, header.Protocol);
        if (!_pending.TryGetValue(key, out var buffer))
        {
            buffer = new Buffer();
            _pending[key] = buffer;
        }

        buffer.LastSeen = now;
        fragment.CopyTo(buffer.Data.AsSpan(header.FragmentOffset));
        buffer.Ranges.Add((header.FragmentOffset, header.FragmentOffset + fragment.Length));
        if (!header.MoreFragments)
        {
            buffer.TotalLength = header.FragmentOffset + fragment.Length;
        }

        if (buffer.TotalLength is not { } total || !IsComplete(buffer.Ranges, total))
        {
            return null;
        }

        _pending.Remove(key);
        return buffer.Data.AsSpan(0, total).ToArray();
    }

    private static bool IsComplete(List<(int Start, int End)> ranges, int total)
    {
        var covered = 0;
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            if (start > covered)
            {
                return false;
            }

            covered = Math.Max(covered, end);
        }

        return covered >= total;
    }

    private sealed class Buffer
    {
        public byte[] Data { get; } = new byte[65535];
        public List<(int Start, int End)> Ranges { get; } = new();
        public int? TotalLength { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
