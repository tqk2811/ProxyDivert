using System;
using System.Globalization;
using System.Windows.Data;
using ProxyDivert.Wpf.Localization;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>
/// Localized text for somewhere <c>DynamicResource</c> cannot reach — a
/// <see cref="System.Windows.Controls.DataGridColumn"/>'s <c>Header</c> above all.
/// </summary>
/// <remarks>
/// A column is outside the visual tree, so it never receives the invalidation that a dictionary
/// swap sends: written as <c>{DynamicResource Str.App.Type}</c> a header resolves once, when the
/// grid is first realized, and then keeps whichever language was active at that moment for the
/// rest of the session. Switching language moves every other label and leaves the headers behind.
///
/// This binds to <see cref="LocalizationScope.Version"/> instead, whose <c>Source</c> is a static
/// object — no tree needed — and which changes with the language, so the lookup runs again. Use it
/// as <c>Header="{b:LocalizedBinding Str.App.Type}"</c>.
/// </remarks>
public sealed class LocalizedBinding : Binding
{
    public LocalizedBinding(string key)
        : base(nameof(LocalizationScope.Version))
    {
        Source = LocalizationScope.Instance;
        Mode = BindingMode.OneWay;
        Converter = KeyLookup;
        ConverterParameter = key ?? throw new ArgumentNullException(nameof(key));
    }

    private static readonly IValueConverter KeyLookup = new StringResourceConverter();

    // The bound value is only the change signal; the text comes from the key in the parameter.
    private sealed class StringResourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => LocalizationManager.Get(parameter as string ?? string.Empty);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("Display text only.");
    }
}
