using System.Collections;

namespace LootLogger.Core.Protocol;

/// <summary>Преобразование значений параметров Photon в нужные типы.</summary>
public static class Values
{
    public static long? ToLong(object? value)
    {
        return value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            _ => null
        };
    }

    public static int ToInt(object? value)
    {
        return value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v when v <= int.MaxValue => (int) v,
            long v when v is >= int.MinValue and <= int.MaxValue => (int) v,
            _ => 0
        };
    }

    public static bool ToBool(object? value)
    {
        return value as bool? ?? false;
    }

    public static string ToText(object? value)
    {
        return value?.ToString() ?? string.Empty;
    }

    public static Guid? ToGuid(object? value)
    {
        try
        {
            switch (value)
            {
                case byte[] { Length: 16 } bytes:
                    return new Guid(bytes);
                case sbyte[] { Length: 16 } sbytes:
                    var converted = new byte[16];
                    for (var i = 0; i < 16; i++)
                    {
                        converted[i] = unchecked((byte) sbytes[i]);
                    }

                    return new Guid(converted);
                case IEnumerable enumerable and not string:
                    var list = new List<byte>(16);
                    foreach (var item in enumerable)
                    {
                        switch (item)
                        {
                            case byte b:
                                list.Add(b);
                                break;
                            case sbyte sb:
                                list.Add(unchecked((byte) sb));
                                break;
                            default:
                                return null;
                        }
                    }

                    return list.Count == 16 ? new Guid(list.ToArray()) : null;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    public static List<long> ToLongList(object? value)
    {
        var result = new List<long>();
        if (value is null or string || value is not IEnumerable enumerable)
        {
            return result;
        }

        foreach (var item in enumerable)
        {
            var number = ToLong(item);
            if (number is not null)
            {
                result.Add(number.Value);
            }
        }

        return result;
    }

    public static List<string> ToStringList(object? value)
    {
        return value switch
        {
            string[] strings => [.. strings],
            object[] objects => objects.Select(x => x?.ToString() ?? string.Empty).ToList(),
            _ => []
        };
    }

    public static List<Guid> ToGuidList(object? value)
    {
        var result = new List<Guid>();
        switch (value)
        {
            case byte[] bytes:
                for (var offset = 0; offset + 16 <= bytes.Length; offset += 16)
                {
                    result.Add(new Guid(bytes.AsSpan(offset, 16)));
                }

                break;
            case object[] objects:
                foreach (var item in objects)
                {
                    var guid = ToGuid(item);
                    if (guid is not null)
                    {
                        result.Add(guid.Value);
                    }
                }

                break;
        }

        return result;
    }
}
