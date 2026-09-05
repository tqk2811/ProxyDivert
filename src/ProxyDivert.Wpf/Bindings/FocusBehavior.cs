using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>Puts the caret in a box the moment the row holding it appears.</summary>
/// <remarks>
/// Adding a condition should be one click and then typing. Without this the new row appears empty
/// and unfocused, and every single one costs a click to get into — which is exactly the friction
/// that makes people give up on a tree editor and ask for a text box instead.
///
/// It writes false back through the binding once it has focused, so the row does not grab focus
/// again the next time its template is rebuilt — which happens on its own whenever rows are
/// grouped, ungrouped or moved.
/// </remarks>
public static class FocusBehavior
{
    public static readonly DependencyProperty FocusWhenProperty =
        DependencyProperty.RegisterAttached(
            "FocusWhen",
            typeof(bool),
            typeof(FocusBehavior),
            new FrameworkPropertyMetadata(
                false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFocusWhenChanged));

    public static void SetFocusWhen(DependencyObject element, bool value)
        => element.SetValue(FocusWhenProperty, value);

    public static bool GetFocusWhen(DependencyObject element)
        => (bool)element.GetValue(FocusWhenProperty);

    private static void OnFocusWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;

        // The element is not in the tree yet at the moment the binding lands on it, so focusing
        // now would go nowhere. Input priority is after layout, when it can actually take focus.
        element.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                element.Focus();
                (element as TextBox)?.SelectAll();
                SetFocusWhen(element, false);
            }));
    }
}
