using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Converters;

/// <summary>
/// What the tunnel button offers this outbound: Disconnect while a tunnel is being held up,
/// Connect otherwise.
/// </summary>
/// <remarks>
/// Whether there is a tunnel is the row's own answer now — it used to be looked up here, from the
/// outbound's id against a separate list, with the list's count bound as well because a collection
/// that gains an item raises nothing on the property holding it. What is left is the wording, and
/// the language version alongside it: a converter's output does not follow a DynamicResource, so
/// without it the button would keep whichever language was loaded when the row was built.
/// </remarks>
public sealed class VpnConnectButtonTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool connected = values.Length > 0 && values[0] is true;
        return LocalizationManager.Get(connected ? "Str.Vpn.Disconnect" : "Str.Vpn.Connect");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display only.");
}
