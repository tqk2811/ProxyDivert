using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.Views;
using Xunit;

namespace ProxyDivert.Core.Tests;

// WPF allows one Application per process, and these two classes both build it; the collection
// keeps them off each other's thread rather than racing to be the one that creates it.
[CollectionDefinition("WPF")]
public sealed class WpfCollection { }

// A DataGrid column is neither a visual nor a logical child of its grid, so a binding on one of its
// own properties has no ancestor to walk. Written as "RelativeSource AncestorType=UserControl" it
// resolves to nothing and the column's ItemsSource stays null: the drop-down opens empty, and
// neither the build nor the log says a word. Only running the view catches it — so this does.
[Collection("WPF")]
public class DataGridColumnBindingTests
{
    // Every list the combo columns across the three views ask for, under the names they use.
    private sealed class ViewModelStub
    {
        public Array Kinds { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.OutboundKind));
        public Array VpnProtocols { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Vpn.Enums.VpnProtocol));
        public Array Ipv6Supports { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.Ipv6Support));
        public Array Matchers { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.HostMatcherType));
        public Array ArgumentMatchers { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.ArgumentMatcherType));
        public Array UdpModes { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.UdpMode));
        public ObservableCollection<object> Policies { get; } = new();
        public ObservableCollection<object> Outbounds { get; } = new();
        public ObservableCollection<object> Rules { get; } = new();
        public ObservableCollection<object> AppliedProcesses { get; } = new();
        public ObservableCollection<object> Connections { get; } = new();
    }

    // A theme setter for IsReadOnly applies to every grid in the application, and it did: the
    // outbound URL, its username and password, a rule's pattern and every check box could not be
    // typed into at all. The combo columns went on working — their display element is a live
    // ComboBox, edit mode or not — so the tab looked like it accepted edits and lost them.
    //
    // The grids are named here rather than walked, because the point is which of them is meant to
    // be written in and which is a read-out. Getting that backwards is the bug.
    [Fact]
    public void The_grids_that_are_meant_to_be_edited_accept_edits()
    {
        var readOnly = new List<string>();
        var writable = new List<string>();

        RunOnStaThread(() =>
        {
            EnsureApplication();

            foreach (UserControl view in new UserControl[]
                     { new OutboundsView(), new ProcessesView(), new RulesView(), new ConnectionsView() })
            {
                view.DataContext = new ViewModelStub();
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                view.UpdateLayout();

                foreach (DataGrid grid in FindVisuals<DataGrid>(view))
                {
                    string name = view.GetType().Name;
                    (grid.IsReadOnly ? readOnly : writable).Add(name);

                    // A column can refuse on its own even where the grid does not — a one-way
                    // binding is enough to do it — and the cells that were stuck are exactly these
                    // two kinds. Asked only of the grids that are open: a column takes its grid's
                    // answer when the grid has already said no, so the read-out would report every
                    // column of its own as a fault.
                    if (grid.IsReadOnly) continue;
                    foreach (DataGridColumn column in grid.Columns)
                        if (column.IsReadOnly && column is DataGridTextColumn or DataGridCheckBoxColumn)
                            readOnly.Add($"{name}: column '{column.Header}'");
                }

                window.Close();
            }
        });

        Assert.Equal(new[] { "OutboundsView", "ProcessesView", "RulesView" }, writable);

        // The connection list is a read-out of what the engine is doing; nothing in it is a setting.
        Assert.Equal(new[] { "ConnectionsView" }, readOnly);
    }

    // Which VPN to speak is a question only a VPN row can answer, and the two built-in outbounds
    // answer no questions at all. Both are said in XAML triggers, which the compiler never checks:
    // bind the trigger to a property that is not there and every row simply comes back enabled.
    //
    // The built-in half is also where this went wrong once already. Disabling the whole row does
    // hold them — but a fresh configuration contains Direct and Block and nothing else, so the tab
    // opened on a grid that was entirely grey and looked switched off. Hence the last two checks:
    // the rows stay alive, they just refuse the edit.
    [Fact]
    public void The_pickers_are_live_only_where_the_setting_means_something()
    {
        bool vpnRowLive = false;
        bool proxyRowLive = false;
        bool builtInVpnLive = false;
        bool builtInKindLive = false;
        string builtInKindText = string.Empty;
        bool proxyKindLive = false;
        bool builtInCheckLive = false;
        bool builtInRowEnabled = false;
        bool builtInAcceptedEdit = false;
        bool proxyAcceptedEdit = false;

        RunOnStaThread(() =>
        {
            EnsureApplication();

            var direct = ProxyDivert.Core.Routing.Models.Outbound.CreateDirect();
            var proxy = new ProxyDivert.Core.Routing.Models.Outbound
            {
                Id = Guid.NewGuid(),
                Name = "proxy 1",
                Kind = OutboundKind.Socks5,
                Url = "socks5://127.0.0.1:1080",
            };
            var vpn = new ProxyDivert.Core.Routing.Models.Outbound
            {
                Id = Guid.NewGuid(),
                Name = "office",
                Kind = OutboundKind.Vpn,
                Url = "sstp://vpn.example.com:443",
            };

            var stub = new ViewModelStub();
            foreach (object outbound in new object[] { direct, proxy, vpn }) stub.Outbounds.Add(outbound);

            var view = new OutboundsView { DataContext = stub };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            DataGrid grid = FindVisuals<DataGrid>(view).First();
            DataGridColumn vpnProtocol = ComboColumnFor(grid, "VpnProtocol");
            DataGridColumn kind = grid.Columns.OfType<DataGridTemplateColumn>().Single();
            DataGridColumn url = grid.Columns
                .OfType<DataGridTextColumn>()
                .Single(c => (c.Binding as System.Windows.Data.Binding)?.Path.Path == "Url");
            DataGridColumn enabled = grid.Columns.OfType<DataGridCheckBoxColumn>().Single();

            vpnRowLive = PickerIsLive(grid, vpn, vpnProtocol);
            proxyRowLive = PickerIsLive(grid, proxy, vpnProtocol);
            builtInVpnLive = PickerIsLive(grid, direct, vpnProtocol);

            // The type cell is a template column: the built-in rows show what they are as text,
            // because their own kinds are not in the picker's list and it would otherwise sit there
            // blank. So "not live" here means the picker is not shown at all.
            builtInKindLive = FindVisuals<ComboBox>(CellFor(grid, direct, kind))
                .Any(combo => combo.IsVisible);
            builtInKindText = FindVisuals<TextBlock>(CellFor(grid, direct, kind))
                .Where(text => text.IsVisible)
                .Select(text => text.Text)
                .FirstOrDefault() ?? string.Empty;
            proxyKindLive = FindVisuals<ComboBox>(CellFor(grid, proxy, kind)).Single().IsVisible;

            builtInCheckLive = FindVisuals<CheckBox>(CellFor(grid, direct, enabled)).Single().IsEnabled;
            builtInRowEnabled = RowFor(grid, direct).IsEnabled;
            builtInAcceptedEdit = TryEdit(grid, direct, url);
            proxyAcceptedEdit = TryEdit(grid, proxy, url);

            window.Close();
        });

        Assert.True(vpnRowLive, "A VPN row cannot pick which VPN it speaks.");
        Assert.False(proxyRowLive, "A SOCKS row offers a VPN protocol picker that changes nothing.");
        Assert.False(builtInVpnLive, "Direct offers a VPN protocol picker.");
        Assert.False(builtInKindLive, "Direct can be turned into something else, breaking every rule pointing at it.");
        Assert.False(string.IsNullOrWhiteSpace(builtInKindText),
            "Direct's type cell is empty, so the row reads as half-filled rather than fixed.");
        Assert.True(proxyKindLive, "An ordinary row cannot change its type.");

        Assert.False(builtInCheckLive, "Direct can be switched off, leaving unmatched traffic nowhere to go.");
        Assert.True(builtInRowEnabled, "The built-in rows are disabled outright, which greys out the whole tab.");
        Assert.False(builtInAcceptedEdit, "Direct's URL can be typed into, and a rule pointing at it would break.");
        Assert.True(proxyAcceptedEdit, "No row can be edited at all — the refusal is not limited to the built-ins.");
    }

    private static DataGridColumn ComboColumnFor(DataGrid grid, string path)
        => grid.Columns
            .OfType<DataGridComboBoxColumn>()
            .Single(c => (c.SelectedItemBinding as System.Windows.Data.Binding)?.Path.Path == path);

    private static DataGridRow RowFor(DataGrid grid, object item)
        => (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);

    // IsEnabled here is the effective one, so this answers the question the user asks with a click:
    // does this picker respond at all.
    private static bool PickerIsLive(DataGrid grid, object item, DataGridColumn column)
    {
        DataGridCell cell = CellFor(grid, item, column);
        return FindVisuals<ComboBox>(cell).Single().IsEnabled;
    }

    // Whether the grid opens an editor in that cell. BeginningEdit is a routed event the view
    // cancels, and nothing but running it says whether the cancellation reaches the right rows.
    private static bool TryEdit(DataGrid grid, object item, DataGridColumn column)
    {
        grid.CurrentCell = new DataGridCellInfo(item, column);
        grid.BeginEdit();
        bool editing = CellFor(grid, item, column).IsEditing;
        grid.CancelEdit();
        return editing;
    }

    private static DataGridCell CellFor(DataGrid grid, object item, DataGridColumn column)
        => FindVisuals<DataGridCell>(RowFor(grid, item)).Single(c => ReferenceEquals(c.Column, column));

    [Fact]
    public void Every_combo_column_resolves_its_items_source()
    {
        var empty = new List<string>();
        int inspected = 0;
        RunOnStaThread(() =>
        {
            EnsureApplication();

            foreach (UserControl view in new UserControl[] { new OutboundsView(), new ProcessesView(), new RulesView() })
            {
                view.DataContext = new ViewModelStub();

                // A window and a layout pass: the columns' bindings are attached when the grid is
                // realized, not when the view is constructed.
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                view.UpdateLayout();

                foreach (DataGrid grid in FindVisuals<DataGrid>(view))
                    foreach (DataGridComboBoxColumn column in grid.Columns.OfType<DataGridComboBoxColumn>())
                    {
                        inspected++;
                        if (column.ItemsSource == null)
                            empty.Add($"{view.GetType().Name}: column '{column.Header}'");
                    }

                window.Close();
            }
        });

        // If the walk ever stops finding the grids, the check above would pass by looking at
        // nothing at all; this is what says it actually looked. Combo boxes that live inside a
        // DataGridTemplateColumn are not counted here — the outbound type picker is one of those
        // now: those sit in the visual tree like any other control, so they cannot have this bug
        // in the first place — and the process filter's policy column is now one of those too, since
        // a filter names several policies in an order a drop-down cannot show.
        //
        // Three left: the outbound type and IPv6 pickers, and the rule matcher. A rule no longer
        // names an outbound — its policy does — so that column is gone.
        Assert.Equal(3, inspected);

        Assert.True(empty.Count == 0,
            "These combo columns resolved no ItemsSource, so their drop-downs would open empty:"
            + Environment.NewLine + string.Join(Environment.NewLine, empty));
    }

    // A cell template is only built once there is a row to build it for — measuring an empty grid,
    // which is all the resource smoke test does, would never touch it. This puts a filter in the
    // grid and checks what the user actually gets in the conditions cell: the tree read back as a
    // sentence, and a button to go and edit it.
    [Fact]
    public void The_conditions_cell_says_what_the_filter_matches()
    {
        var texts = new List<string>();
        int buttons = 0;

        RunOnStaThread(() =>
        {
            EnsureApplication();
            LocalizationManager.Apply(AppLanguage.English);

            var stub = new ViewModelStub();
            stub.Rules.Add(SampleFilter());

            var view = new ProcessesView { DataContext = stub };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            DataGrid grid = FindVisuals<DataGrid>(view).First();
            texts.AddRange(FindVisuals<TextBlock>(grid).Select(block => block.Text ?? string.Empty));
            buttons = FindVisuals<Button>(grid).Count();

            window.Close();
        });

        // The whole tree, brackets and all, in the one cell that replaced two columns of pickers.
        Assert.Contains(texts, text => text.Contains("java.exe") && text.Contains("minecraft")
                                       && text.Contains("AND") && text.Contains("OR"));
        Assert.True(buttons >= 1, "The conditions cell has no button to open the editor.");
    }

    // The condition rows are drawn by a template that contains an ItemsControl over the same kind
    // of thing it is itself, so a group inside a group renders through it again. Nothing about
    // that shows up at build time: get the recursion wrong and the window opens with the nested
    // group simply missing, or with its pickers empty. Only running it says.
    [Fact]
    public void The_filter_editor_draws_a_nested_group_with_every_picker_filled()
    {
        int combos = 0;
        bool policyTicked = false;
        var empty = new List<string>();
        var patterns = new List<string>();

        RunOnStaThread(() =>
        {
            EnsureApplication();

            var policy = new ProxyDivert.Core.Routing.Models.RoutingPolicy
            {
                Id = Guid.NewGuid(),
                Name = "policy",
                OutboundId = Guid.NewGuid(),
            };
            ProxyDivert.Core.Routing.Models.ProcessRule filter = SampleFilter();
            filter.PolicyIds.Add(policy.Id);

            var window = new ProcessFilterWindow(
                new ProxyDivert.Wpf.ViewModels.ProcessFilterViewModel(filter, new[] { policy }))
            {
                Width = 1000,
                Height = 800,
            };
            window.Show();
            window.UpdateLayout();

            foreach (ComboBox combo in FindVisuals<ComboBox>(window))
            {
                combos++;
                if (combo.ItemsSource == null) empty.Add(combo.Name);
            }

            patterns.AddRange(FindVisuals<TextBox>(window).Select(box => box.Text ?? string.Empty));

            // The policy list is an ItemsControl of ticked rows, so a mistyped binding path would
            // leave the window with no way to say where matching traffic goes and nothing to show
            // for it — the box would simply not be there.
            policyTicked = FindVisuals<CheckBox>(window)
                .Any(box => Equals(box.Content, "policy") && box.IsChecked == true);

            window.Close();
        });

        // Eight in the tree — an operator picker on the root and on the nested group, plus the
        // subject and comparison pickers on each of the three condition rows — and one below it,
        // for the process to try the filter against. The policies are a ticked list now, not a
        // picker: their order is what they mean.
        Assert.Equal(9, combos);
        Assert.True(empty.Count == 0, $"{empty.Count} pickers in the filter editor resolved no ItemsSource.");

        // The name box, plus one value box per condition — including the two inside the group.
        Assert.Contains("java.exe", patterns);
        Assert.Contains("minecraft", patterns);
        Assert.Contains("forge", patterns);

        Assert.True(policyTicked, "The filter editor does not show the policy it applies as a ticked row.");
    }

    // java.exe AND (minecraft OR forge) — one bracket inside another, which is the shape the whole
    // editor exists for.
    private static ProxyDivert.Core.Routing.Models.ProcessRule SampleFilter()
        => new ProxyDivert.Core.Routing.Models.ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "Minecraft",
            PolicyIds = { Guid.NewGuid() },
            Condition = new ConditionGroup
            {
                Operator = ConditionOperator.All,
                Children =
                {
                    new ProcessNameCondition
                    {
                        Matcher = ProxyDivert.Core.Routing.Enums.ProcessMatcherType.ExeName,
                        Pattern = "java.exe",
                    },
                    new ConditionGroup
                    {
                        Operator = ConditionOperator.Any,
                        Children =
                        {
                            new CommandLineCondition { Pattern = "minecraft" },
                            new CommandLineCondition { Pattern = "forge" },
                        },
                    },
                },
            },
        };

    // DataGrid.RowHeight is a fixed height, not a minimum, so a cell holding a control taller than
    // a line of text is simply cut off at the bottom — the grid reports no error and the row looks
    // deliberate. The rule row holds two 28px pickers and two 28px text boxes, so this checks the
    // row is actually tall enough to show what is in it.
    [Fact]
    public void A_rule_row_is_tall_enough_for_the_controls_in_it()
    {
        var tooTall = new List<string>();

        RunOnStaThread(() =>
        {
            EnsureApplication();

            var stub = new ViewModelStub();
            stub.Rules.Add(SampleFilter());

            var view = new ProcessesView { DataContext = stub };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            DataGrid grid = FindVisuals<DataGrid>(view).First();
            DataGridRow row = FindVisuals<DataGridRow>(grid).First();

            // The control's own height is not what overflows — the margins between it and the cell
            // are. Walking up and adding them is what says whether the cell can hold it.
            foreach (DataGridCell cell in FindVisuals<DataGridCell>(row))
                foreach (Control control in FindVisuals<ComboBox>(cell).Cast<Control>().Concat(FindVisuals<TextBox>(cell)))
                {
                    double needed = RequiredHeight(control) + MarginsUpTo(control, cell);
                    if (needed > cell.ActualHeight)
                        tooTall.Add($"cell '{cell.Column.Header}' is {cell.ActualHeight}px for content needing {needed}px");
                }

            window.Close();
        });

        Assert.True(tooTall.Count == 0,
            "These rule-row cells are shorter than what they hold, so it is cut off at the bottom:"
            + Environment.NewLine + string.Join(Environment.NewLine, tooTall));
    }

    // What the control actually needs, which DesiredSize alone does not tell you: measured inside a
    // row of fixed height it comes back already clipped to that height, so a control asking for 28
    // in a 26px slot reports 26 and the overflow reads as a perfect fit. An explicit Height is not
    // negotiable, so it wins where the style sets one.
    private static double RequiredHeight(FrameworkElement element)
        => double.IsNaN(element.Height)
            ? element.DesiredSize.Height
            : Math.Max(element.DesiredSize.Height, element.Height);

    // Vertical margin between an element and an ancestor, the ancestor's own padding aside.
    private static double MarginsUpTo(FrameworkElement element, DependencyObject ancestor)
    {
        double total = 0;
        DependencyObject? current = element;
        while (current != null && !ReferenceEquals(current, ancestor))
        {
            if (current is FrameworkElement framework)
                total += framework.Margin.Top + framework.Margin.Bottom;
            current = VisualTreeHelper.GetParent(current);
        }
        return total;
    }

    // A column never receives the invalidation a dictionary swap sends, so "{DynamicResource ...}"
    // on a Header resolves once and then keeps whichever language was active when the grid was
    // first built. Every other label in the window follows the switch and the headers do not —
    // which is exactly what the user saw. LocalizedBinding is what fixes it; this holds it fixed.
    [Fact]
    public void Column_headers_follow_a_language_switch()
    {
        var english = new List<string>();
        var vietnamese = new List<string>();
        var sameInBothLanguages = new HashSet<string>(StringComparer.Ordinal);

        RunOnStaThread(() =>
        {
            EnsureApplication();

            // Gathered first, and on this thread: it swaps dictionaries itself, so doing it after
            // the window exists would blur which swap moved the headers.
            sameInBothLanguages.UnionWith(StringsThatTranslateToThemselves());

            LocalizationManager.Apply(AppLanguage.English);

            var view = new ProcessesView { DataContext = new ViewModelStub() };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            DataGrid grid = FindVisuals<DataGrid>(view).First();
            english.AddRange(grid.Columns.Select(column => column.Header?.ToString() ?? string.Empty));

            LocalizationManager.Apply(AppLanguage.Vietnamese);
            view.UpdateLayout();
            vietnamese.AddRange(grid.Columns.Select(column => column.Header?.ToString() ?? string.Empty));

            window.Close();
        });

        Assert.NotEmpty(english);
        Assert.Equal(english.Count, vietnamese.Count);

        // A header that did not change is the symptom — but a handful of words are spelled the
        // same in both languages, and those are not evidence of anything. Asking the dictionaries
        // which strings translate to themselves keeps that from reading as a failure, without
        // having to hard-code the exceptions here.
        List<string> stuck = english
            .Where((text, i) => text == vietnamese[i] && !sameInBothLanguages.Contains(text))
            .ToList();

        Assert.True(stuck.Count == 0,
            "These column headers kept their English text after switching to Vietnamese: "
            + string.Join(", ", stuck));
    }

    // Must run on the STA thread that owns the application resources.
    private static HashSet<string> StringsThatTranslateToThemselves()
    {
        var identical = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        LocalizationManager.Apply(AppLanguage.English);
        foreach (KeyValuePair<string, string> entry in StringResources())
            byKey[entry.Key] = entry.Value;

        LocalizationManager.Apply(AppLanguage.Vietnamese);
        foreach (KeyValuePair<string, string> entry in StringResources())
            if (byKey.TryGetValue(entry.Key, out string? english) && english == entry.Value)
                identical.Add(english);

        return identical;
    }

    private static IEnumerable<KeyValuePair<string, string>> StringResources()
    {
        foreach (ResourceDictionary dictionary in Application.Current.Resources.MergedDictionaries)
            foreach (object key in dictionary.Keys)
                if (key is string name && dictionary[key] is string value)
                    yield return new KeyValuePair<string, string>(name, value);
    }

    private static Application EnsureApplication()
    {
        if (Application.Current == null)
        {
            var application = new ProxyDivert.Wpf.App();
            application.InitializeComponent();
        }

        // Default is OnLastWindowClose, which would shut the dispatcher down the first time this
        // test closes a window and leave every view after it unloaded — and so unchecked.
        //
        // Only the thread that built the Application may touch it, and each test here runs on an
        // STA thread of its own. That is harmless: a window opened from a thread that does not own
        // the Application is not in its window list, so closing it shuts nothing down either way.
        if (Application.Current.CheckAccess())
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        return Application.Current;
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (T deeper in FindVisuals<T>(child)) yield return deeper;
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new Xunit.Sdk.XunitException($"WPF column test failed: {failure}");
    }
}
