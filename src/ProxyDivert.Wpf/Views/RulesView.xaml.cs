using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();
    }

    // Double-click renames the row under the pointer — not the selected one. They are usually the
    // same, but a double-click on empty space below the list would otherwise put the last selected
    // policy into edit mode from nowhere.
    private void PolicyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not RulesViewModel model) return;
        if (e.OriginalSource is not DependencyObject source) return;

        ListBoxItem? row = FindAncestor<ListBoxItem>(source);
        if (row?.DataContext is not ProxyDivert.Core.Routing.Models.RoutingPolicy policy) return;

        model.BeginRename(policy);

        // The box only exists once the trigger has made it visible, which happens as the binding
        // updates — so the focus waits for that pass rather than racing it.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
        {
            TextBox? box = FindDescendants<TextBox>(row).FirstOrDefault(b => b.IsVisible);
            if (box == null) return;
            box.Focus();
            box.SelectAll();
        }));
    }

    // Both ways of finishing: the box losing focus (clicking another row, or anywhere else) and
    // Enter. Escape drops the edit instead, which is the only way back out without renaming.
    private void PolicyNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        (DataContext as RulesViewModel)?.CommitRename();
    }

    private void PolicyNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not RulesViewModel model) return;

        switch (e.Key)
        {
            case Key.Enter:
                model.CommitRename();
                e.Handled = true;
                break;

            case Key.Escape:
                model.CancelRename();
                e.Handled = true;
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is T hit) return hit;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (T deeper in FindDescendants<T>(child)) yield return deeper;
        }
    }
}
