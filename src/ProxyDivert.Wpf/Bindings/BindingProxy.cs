using System.Windows;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>
/// Carries a DataContext to places the visual tree does not reach — a
/// <see cref="System.Windows.Controls.DataGridComboBoxColumn"/> above all.
/// </summary>
/// <remarks>
/// A DataGrid column is neither a visual nor a logical child of the grid, so a binding on one of
/// its properties has no ancestor to walk: <c>RelativeSource AncestorType=UserControl</c> never
/// resolves, and the column's ItemsSource silently stays null — an empty drop-down with nothing in
/// the build or the log to say why.
///
/// A <see cref="Freezable"/> is the way around it. Put one in an element's resources and WPF gives
/// it that element's inheritance context, DataContext included; the column can then reach the view
/// model through <c>{Binding Data.Xxx, Source={StaticResource ...}}</c>, which needs no tree at all.
/// </remarks>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
