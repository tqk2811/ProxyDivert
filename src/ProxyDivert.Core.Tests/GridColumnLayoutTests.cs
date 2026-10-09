using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ProxyDivert.Bindings;
using ProxyDivert.Services;
using ProxyDivert.Views;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Column widths are remembered by two halves that have to agree: the behavior watching the grid,
// and the key written on each column. Neither is checked by the compiler — a column with no key is
// valid XAML that silently forgets, and the widths are read back from a file nothing else reads —
// so the whole round trip is done here for real: open the view, drag a column, throw the store
// away, and open the view again on what is left on disk.
[Collection("WPF")]
public class GridColumnLayoutTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"proxydivert-layout-{Guid.NewGuid():N}.json");

    // Just enough of a view model for the four grids to bind to. The lists stay empty: what is
    // being checked is the columns, and a column is as wide as it is with no rows under it.
    private sealed class ViewModelStub
    {
        public ViewModelStub(GridLayoutSection layout) => ColumnLayout = layout;

        public GridLayoutSection ColumnLayout { get; }

        public Array Kinds { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.OutboundKind));
        public Array VpnProtocols { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Vpn.Enums.VpnProtocol));
        public Array Ipv6Supports { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.Ipv6Support));
        public Array Matchers { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.HostMatcherType));
        public ObservableCollection<object> Policies { get; } = new ObservableCollection<object>();
        public ObservableCollection<object> Outbounds { get; } = new ObservableCollection<object>();
        public ObservableCollection<object> Rules { get; } = new ObservableCollection<object>();
        public ObservableCollection<object> AppliedProcesses { get; } = new ObservableCollection<object>();
        public ObservableCollection<object> Connections { get; } = new ObservableCollection<object>();
    }

    // A column the user can drag and that nothing remembers is the failure this is here to catch,
    // and it is one nobody would notice: the column comes back its declared width and the drag
    // looks like it was never made. Adding a column to a view is exactly when it happens.
    [Fact]
    public void Every_column_the_user_can_drag_has_a_key()
    {
        var unkeyed = new List<string>();

        using var store = new GridLayoutStore(_path);

        WpfHost.RunOnStaThread(() =>
        {
            WpfHost.EnsureApplication();

            foreach (UserControl view in new UserControl[]
                     { new OutboundsView(), new ProcessesView(), new RulesView(), new ConnectionsView() })
            {
                view.DataContext = new ViewModelStub(store.Section("probe"));
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                view.UpdateLayout();

                foreach (DataGrid grid in WpfHost.Descendants<DataGrid>(view))
                {
                    // The grid itself first: with no layout section not one of its columns is
                    // watched, whatever the columns say.
                    if (ColumnLayoutBehavior.GetLayout(grid) is null)
                        unkeyed.Add($"{view.GetType().Name}: the grid itself");

                    foreach (DataGridColumn column in grid.Columns)
                    {
                        // A column that cannot be resized has one width for ever, so there is
                        // nothing to remember. The drag grip on the filter grid is the one.
                        if (!column.CanUserResize) continue;

                        if (string.IsNullOrEmpty(ColumnLayoutBehavior.GetColumnKey(column)))
                            unkeyed.Add($"{view.GetType().Name}: column '{column.Header}'");
                    }
                }

                window.Close();
            }
        }, nameof(Every_column_the_user_can_drag_has_a_key));

        Assert.Empty(unkeyed);
    }

    // The point of the feature, end to end. The second store reads the file the first one wrote,
    // which is what surviving a restart comes down to: nothing is carried between the two halves
    // of this test in memory.
    [Fact]
    public void A_width_dragged_on_one_run_comes_back_on_the_next()
    {
        const double Dragged = 321;
        double reopenedWidth = 0;

        using (var first = new GridLayoutStore(_path))
        {
            GridLayoutSection section = first.Section("connections");

            WpfHost.RunOnStaThread(() =>
            {
                WpfHost.EnsureApplication();

                var view = new ConnectionsView { DataContext = new ViewModelStub(section) };
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                view.UpdateLayout();

                DataGridColumn host = ColumnNamed(view, "host");
                host.Width = new DataGridLength(Dragged, DataGridLengthUnitType.Pixel);
                view.UpdateLayout();

                window.Close();
            }, "dragging the column");

            // What the drag left in memory, before any of it has been written.
            Assert.Equal(Dragged, section.WidthOf("host"));
        }

        // A second store over the same file, sharing nothing with the first but the path.
        using var second = new GridLayoutStore(_path);
        Assert.Equal(Dragged, second.Section("connections").WidthOf("host"));

        GridLayoutSection restored = second.Section("connections");

        WpfHost.RunOnStaThread(() =>
        {
            WpfHost.EnsureApplication();

            var view = new ConnectionsView { DataContext = new ViewModelStub(restored) };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            reopenedWidth = ColumnNamed(view, "host").ActualWidth;

            window.Close();
        }, "reopening the view");

        Assert.Equal(Dragged, reopenedWidth);
    }

    // A column nobody has touched keeps the width its view declares: the file holds what was
    // dragged, not a copy of the whole arrangement.
    [Fact]
    public void An_untouched_column_keeps_the_width_from_the_view()
    {
        double pidWidth = 0;

        using var store = new GridLayoutStore(_path);
        GridLayoutSection section = store.Section("connections");

        WpfHost.RunOnStaThread(() =>
        {
            WpfHost.EnsureApplication();

            var view = new ConnectionsView { DataContext = new ViewModelStub(section) };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            pidWidth = ColumnNamed(view, "pid").ActualWidth;

            window.Close();
        }, nameof(An_untouched_column_keeps_the_width_from_the_view));

        Assert.Equal(70, pidWidth);
    }

    private static DataGridColumn ColumnNamed(DependencyObject view, string key)
        => WpfHost.Descendants<DataGrid>(view)
            .SelectMany(grid => grid.Columns)
            .First(column => ColumnLayoutBehavior.GetColumnKey(column) == key);

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
