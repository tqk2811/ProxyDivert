using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.Localization;

namespace ProxyDivert.Wpf.Converters;

/// <summary>
/// The policies a filter applies, named and in priority order: "Work → Streaming".
/// </summary>
/// <remarks>
/// A filter stores policy ids, and a grid cell has to show names — so the list of policies is the
/// second binding. The third is the language version, which is what re-runs this when the
/// dictionary is swapped; a converter's output does not follow a DynamicResource on its own.
/// </remarks>
public sealed class PolicyNamesConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        IEnumerable<Guid> ids = values.Length > 0 && values[0] is IEnumerable rawIds
            ? rawIds.OfType<Guid>()
            : Enumerable.Empty<Guid>();

        List<RoutingPolicy> policies = values.Length > 1 && values[1] is IEnumerable rawPolicies
            ? rawPolicies.OfType<RoutingPolicy>().ToList()
            : new List<RoutingPolicy>();

        // A policy the user deleted is dropped rather than shown as a blank arrow: the routing does
        // the same thing, and a cell that says "Work → " reads as a bug in the cell.
        List<string> names = ids
            .Select(id => policies.FirstOrDefault(p => p.Id == id))
            .Where(policy => policy != null)
            .Select(policy => policy!.Name)
            .ToList();

        return names.Count > 0
            ? string.Join(" → ", names)
            : LocalizationManager.Get("Str.Process.NoPolicy");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Display text only.");
}
