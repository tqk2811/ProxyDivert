using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Converters;

/// <summary>
/// The tunnel row belonging to one outbound, or <c>null</c> when the engine holds none for it.
/// </summary>
/// <remarks>
/// The grid's rows are outbounds and the tunnels are a separate list keyed by outbound id, so the
/// status cell has to do the lookup itself. The second binding is that list and the third is its
/// count: a converter is re-run when one of its bindings changes, and a collection that gains or
/// loses a tunnel raises nothing on the collection property itself — the count is what notices.
/// The returned view model raises its own changes, so a tunnel going up or down needs no help.
/// </remarks>
public sealed class VpnTunnelForOutboundConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not Guid id || values[1] is not IEnumerable tunnels)
            return null;

        return tunnels.OfType<VpnTunnelViewModel>().FirstOrDefault(t => t.Id == id);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display only.");
}
