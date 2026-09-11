using System;
using System.Globalization;
using System.Windows.Data;
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

// Text for a condition row's subject: its name in the combo box, or — with the parameter "Hint" —
// the hint in the value box next to it. A subject is an object, not an enum value, so EnumText
// cannot name it. Same second binding as above, for the same reason.
public sealed class ConditionSubjectTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not ConditionSubject subject) return string.Empty;

        return parameter as string == "Hint"
            ? ConditionTextBuilder.PatternHint(subject)
            : ConditionTextBuilder.SubjectText(subject);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display text only.");
}
