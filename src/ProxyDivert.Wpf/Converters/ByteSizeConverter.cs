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

// How long a connection lasted, as a short duration ("3.2s", "1m 20s").
//
// Both interfaces, because the answer needs two facts and only one of them is always there. Bound
// to StartedUtc alone it can only measure up to now, which is right for a connection that is still
// open and wrong for every row in the list below it: a connection that ended twenty minutes ago
// went on counting, so the column read as a clock rather than as how long the transfer took. The
// MultiBinding takes StartedUtc and EndedUtc; the single-value form is kept for anything that only
// has a start.
public sealed class DurationConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Format(value as DateTime?, ended: null);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => Format(
            values.Length > 0 ? values[0] as DateTime? : null,
            values.Length > 1 ? values[1] as DateTime? : null);

    private static string Format(DateTime? started, DateTime? ended)
    {
        TimeSpan elapsed = (ended ?? DateTime.UtcNow) - (started ?? DateTime.UtcNow);
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        return elapsed.TotalMinutes < 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.0}s", elapsed.TotalSeconds)
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
