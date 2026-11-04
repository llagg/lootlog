using System.Buffers.Binary;
using System.Text;

namespace LootLogger.Tests;

/// <summary>Собирает настоящие Photon/Protocol18-пакеты для тестов, байт в байт как их шлёт игра.</summary>
internal static class PhotonBuilder
{
    public static byte[] Packet(short peerId, int challenge, params byte[][] commands)
    {
        var bytes = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteInt16BigEndian(header, peerId);
        header[2] = 0; // без CRC и шифрования
        header[3] = (byte) commands.Length;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 123456);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), challenge);
        bytes.AddRange(header);
        foreach (var command in commands)
        {
            bytes.AddRange(command);
        }

        return bytes.ToArray();
    }

    public static byte[] Reliable(int sequence, byte messageType, byte[] body, byte channel = 0)
    {
        var payload = new List<byte> { 0xF3, messageType };
        payload.AddRange(body);
        var header = new byte[12];
        header[0] = 6; // SendReliable
        header[1] = channel;
        header[2] = 1;
        header[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 12 + payload.Count);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), sequence);
        return header.Concat(payload).ToArray();
    }

    public static byte[] Fragment(int sequence, int startSequence, int fragmentCount, int fragmentNumber, int totalLength, int fragmentOffset, byte[] data, byte channel = 0)
    {
        var payload = new byte[20 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), startSequence);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), fragmentCount);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), fragmentNumber);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), totalLength);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), fragmentOffset);
        data.CopyTo(payload, 20);

        var header = new byte[12];
        header[0] = 8; // SendFragment
        header[1] = channel;
        header[2] = 1;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 12 + payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), sequence);
        return header.Concat(payload).ToArray();
    }

    /// <summary>Тело команды для фрагментации: служебный байт, тип сообщения и само сообщение.</summary>
    public static byte[] MessageBody(byte messageType, byte[] body)
    {
        return new byte[] { 0xF3, messageType }.Concat(body).ToArray();
    }

    public static byte[] Event(short eventCode, Dictionary<byte, object> parameters)
    {
        parameters[252] = eventCode;
        var bytes = new List<byte> { 1 };
        WriteTable(bytes, parameters);
        return bytes.ToArray();
    }

    public static byte[] Request(short operationCode, Dictionary<byte, object> parameters)
    {
        parameters[253] = operationCode;
        var bytes = new List<byte> { 1 };
        WriteTable(bytes, parameters);
        return bytes.ToArray();
    }

    public static byte[] Response(short operationCode, Dictionary<byte, object> parameters)
    {
        parameters[253] = operationCode;
        var bytes = new List<byte> { 1, 0, 0, 8 }; // код операции, returnCode=0, debugMessage=null
        WriteTable(bytes, parameters);
        return bytes.ToArray();
    }

    public static byte[] EventPacket(int sequence, short eventCode, Dictionary<byte, object> parameters, short peerId = 1, int challenge = 777)
    {
        return Packet(peerId, challenge, Reliable(sequence, 4, Event(eventCode, parameters)));
    }

    public static byte[] RequestPacket(int sequence, short operationCode, Dictionary<byte, object> parameters)
    {
        return Packet(1, 555, Reliable(sequence, 2, Request(operationCode, parameters)));
    }

    public static byte[] ResponsePacket(int sequence, short operationCode, Dictionary<byte, object> parameters)
    {
        return Packet(1, 777, Reliable(sequence, 3, Response(operationCode, parameters)));
    }

    private static void WriteTable(List<byte> bytes, Dictionary<byte, object> parameters)
    {
        bytes.Add((byte) parameters.Count);
        foreach (var (key, value) in parameters)
        {
            bytes.Add(key);
            WriteValue(bytes, value);
        }
    }

    private static void WriteValue(List<byte> bytes, object value)
    {
        switch (value)
        {
            case string s:
                bytes.Add(7);
                var utf8 = Encoding.UTF8.GetBytes(s);
                WriteVarUInt(bytes, (ulong) utf8.Length);
                bytes.AddRange(utf8);
                break;
            case bool b:
                bytes.Add(2);
                bytes.Add(b ? (byte) 1 : (byte) 0);
                break;
            case short sh:
                bytes.Add(4);
                bytes.Add((byte) (sh & 0xFF));
                bytes.Add((byte) ((sh >> 8) & 0xFF));
                break;
            case int i:
                bytes.Add(9);
                WriteVarUInt(bytes, (uint) ((i << 1) ^ (i >> 31)));
                break;
            case long l:
                bytes.Add(10);
                WriteVarUInt(bytes, (ulong) ((l << 1) ^ (l >> 63)));
                break;
            case byte[] array:
                bytes.Add(67);
                WriteVarUInt(bytes, (ulong) array.Length);
                bytes.AddRange(array);
                break;
            case string[] strings:
                bytes.Add(71);
                WriteVarUInt(bytes, (ulong) strings.Length);
                foreach (var s in strings)
                {
                    var b = Encoding.UTF8.GetBytes(s);
                    WriteVarUInt(bytes, (ulong) b.Length);
                    bytes.AddRange(b);
                }

                break;
            case long[] longs:
                bytes.Add(74);
                WriteVarUInt(bytes, (ulong) longs.Length);
                foreach (var l in longs)
                {
                    WriteVarUInt(bytes, (ulong) ((l << 1) ^ (l >> 63)));
                }

                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
    }

    private static void WriteVarUInt(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte) (value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte) value);
    }
}
