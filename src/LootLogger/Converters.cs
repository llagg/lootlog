using System.Globalization;
using System.Windows.Data;

namespace LootLogger;

/// <summary>Показывает время UTC в местном часовом поясе.</summary>
public sealed class UtcToLocalConverter : IValueConverter
{
    public static UtcToLocalConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DateTime utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
