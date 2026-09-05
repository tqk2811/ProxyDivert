using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    }

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
        // DataGridTemplateColumn are not counted here: those sit in the visual tree like any
        // other control, so they cannot have this bug in the first place.
        Assert.Equal(6, inspected);

        Assert.True(empty.Count == 0,
            "These combo columns resolved no ItemsSource, so their drop-downs would open empty:"
            + Environment.NewLine + string.Join(Environment.NewLine, empty));
    }

    // The process rule's kind and value live together in one DataGridTemplateColumn, and a cell
    // template is only built once there is a row to build it for — measuring an empty grid, which
    // is all the resource smoke test does, would never touch it. This puts a rule in the grid and
    // checks what the user actually gets: two pickers with something in them, and two text boxes.
    [Fact]
    public void The_merged_rule_cell_gives_both_pickers_their_choices()
    {
        var combos = new List<ComboBox>();
        var boxes = new List<TextBox>();

        RunOnStaThread(() =>
        {
            EnsureApplication();

            var stub = new ViewModelStub();
            stub.Rules.Add(new ProxyDivert.Core.Routing.Models.ProcessRule
            {
                Id = Guid.NewGuid(),
                Matcher = ProxyDivert.Core.Routing.Enums.ProcessMatcherType.ExeName,
                Pattern = "chrome.exe",
                PolicyId = Guid.NewGuid(),
            });

            var view = new ProcessesView { DataContext = stub };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            view.UpdateLayout();

            DataGrid grid = FindVisuals<DataGrid>(view).First();
            combos.AddRange(FindVisuals<ComboBox>(grid));
            boxes.AddRange(FindVisuals<TextBox>(grid));

            window.Close();
        });

        // One picker for the process kind, one for the argument kind, one for the policy.
        Assert.Equal(3, combos.Count);
        Assert.All(combos, combo => Assert.NotNull(combo.ItemsSource));

        // The value next to each picker.
        Assert.Equal(2, boxes.Count);
    }

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
            stub.Rules.Add(new ProxyDivert.Core.Routing.Models.ProcessRule
            {
                Id = Guid.NewGuid(),
                Matcher = ProxyDivert.Core.Routing.Enums.ProcessMatcherType.ExeName,
                Pattern = "chrome.exe",
                PolicyId = Guid.NewGuid(),
            });

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
