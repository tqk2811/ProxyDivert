using System;
using System.Globalization;
using System.Windows.Data;
using ProxyDivert.Wpf.Localization;

namespace ProxyDivert.Wpf.Converters;

// Turns an enum value into the localized text for it. Static XAML text uses DynamicResource and
// updates itself, but a combo box lists enum values rather than resource keys, so the display text
// has to be looked up per item.
//
// Both converters take their value through a MultiBinding whose second source is
// LocalizationScope.Version, which is what makes them run again when the language changes — see
// that class for why the option lists are not simply rebuilt instead.
public sealed class EnumLocalizeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => LocalizationManager.EnumText(values.Length > 0 ? values[0] : null);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display text only.");
}

// Display text for the language picker. Each language is named in its own language — someone
// looking for Vietnamese should not have to know the English word for it — and System says which
// language it currently resolves to, so the choice is not a guess.
public sealed class AppLanguageDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is AppLanguage language
            ? language switch
            {
                AppLanguage.English => "English",
                AppLanguage.Vietnamese => "Tiếng Việt",
                _ => $"{LocalizationManager.Get("Str.Lang.System")} ({LocalizationManager.SystemLanguageName})",
            }
            : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display text only.");
}
