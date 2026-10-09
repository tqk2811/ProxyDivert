using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProxyDivert.Helpers;

/// <summary>
/// Shift + mouse wheel scrolls every DataGrid, ListBox/ListView and TreeView sideways, as it does in most
/// Windows applications. WPF's ScrollViewer has no such gesture, so a class handler adds it once
/// for the whole application.
/// </summary>
public static class HorizontalWheelScroll
{
    // One wheel notch (Delta 120) moves about three 16px lines.
    private const double PixelsPerDelta = 0.4;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(DataGrid), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        EventManager.RegisterClassHandler(typeof(ListBox), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        EventManager.RegisterClassHandler(typeof(TreeView), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Shift)
            return;

        var scrollViewer = FindScrollViewer((DependencyObject)sender);
        // Nothing to scroll sideways: leave the wheel to whoever else wants it (an outer grid, say).
        if (scrollViewer is null || scrollViewer.ScrollableWidth <= 0)
            return;

        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta * PixelsPerDelta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer)
                return scrollViewer;
            var found = FindScrollViewer(child);
            if (found is not null)
                return found;
        }
        return null;
    }
}
