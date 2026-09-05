using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Helpers;

namespace ProxyDivert.Wpf.Converters;

// The filter list shows its conditions as the sentence they read as, so the column has to be
// rebuilt whenever the language changes. Like the enum converters next door, the second binding is
// LocalizationScope.Version and exists only to make this run again.
public sealed class ConditionSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => ConditionTextBuilder.Describe(values.Length > 0 ? values[0] as ProcessCondition : null);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display text only.");
}

// The stripe down the side of a row after the filter has been tried against a running process.
//
// Unknown gets its own colour rather than sharing with "no". They are different answers, and the
// difference is the one the user needs: "this row said no" is a filter to fix, while "this row
// could not read the command line of that process" is not a filter problem at all.
public sealed class ConditionResultBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ConditionResult result
            ? result switch
            {
                ConditionResult.Match => Key("Brush.Success"),
                ConditionResult.NoMatch => Key("Brush.Danger"),
                ConditionResult.Unknown => Key("Brush.Warning"),
                _ => Brushes.Transparent,
            }
            : Brushes.Transparent;

    private static object Key(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? (Brush)Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display only.");
}
