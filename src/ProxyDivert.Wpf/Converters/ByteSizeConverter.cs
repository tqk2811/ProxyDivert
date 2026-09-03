using System;
using System.Globalization;
using System.Windows.Data;

namespace ProxyDivert.Wpf.Converters;

// Formats a byte count for a table cell: "0", "812 B", "1.4 MB". Traffic columns are scanned, not
// read, so the unit matters more than the exact digits.
public sealed class ByteSizeConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return string.Empty;
        if (bytes <= 0) return "0";

        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        // Whole numbers for plain bytes; one decimal once a unit prefix is in play.
        return unit == 0
            ? $"{bytes} {Units[0]}"
            : string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", size, Units[unit]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Formats the age of a connection as a short duration ("3.2s", "1m 20s").
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        DateTime started = value switch
        {
            DateTime dt => dt,
            _ => DateTime.UtcNow,
        };
        TimeSpan elapsed = DateTime.UtcNow - started;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        return elapsed.TotalMinutes < 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.0}s", elapsed.TotalSeconds)
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
