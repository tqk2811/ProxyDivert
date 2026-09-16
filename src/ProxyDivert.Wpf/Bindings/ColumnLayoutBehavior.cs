using System.Windows;
using System.Windows.Controls;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>
/// Remembers how wide the user dragged each column of a grid, and puts the columns back that way
/// the next time the grid is shown — including the next time the application is started.
/// </summary>
/// <remarks>
/// A DataGrid has no event for "a column was resized", so the widths are watched one column at a
/// time; see the attachment for how. Two properties make a grid remember: <c>Layout</c> on the grid
/// says where its widths are kept, and <c>ColumnKey</c> names each column inside that.
///
/// The key is written out rather than taken from the column's position or its header. A header is a
/// localized string that changes with the language, and a position shifts the day a column is
/// inserted — either would quietly hand a column the width of a different one. A column with no key
/// is simply not remembered, which is what the fixed-width ones (a drag grip) want anyway.
/// </remarks>
public static partial class ColumnLayoutBehavior
{
    // ==== where a grid's widths are kept ====

    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.RegisterAttached(
            "Layout", typeof(GridLayoutSection), typeof(ColumnLayoutBehavior),
            new PropertyMetadata(null, OnLayoutChanged));

    public static void SetLayout(DependencyObject element, GridLayoutSection? value)
        => element.SetValue(LayoutProperty, value);

    public static GridLayoutSection? GetLayout(DependencyObject element)
        => (GridLayoutSection?)element.GetValue(LayoutProperty);

    // ==== what a column is called in there ====

    public static readonly DependencyProperty ColumnKeyProperty =
        DependencyProperty.RegisterAttached(
            "ColumnKey", typeof(string), typeof(ColumnLayoutBehavior), new PropertyMetadata(null));

    public static void SetColumnKey(DependencyObject element, string? value)
        => element.SetValue(ColumnKeyProperty, value);

    public static string? GetColumnKey(DependencyObject element)
        => (string?)element.GetValue(ColumnKeyProperty);

    // The live wiring for one grid. Private: it exists because the grid was given a layout, and
    // nothing else has any business holding it.
    private static readonly DependencyProperty AttachmentProperty =
        DependencyProperty.RegisterAttached(
            "Attachment", typeof(Attachment), typeof(ColumnLayoutBehavior), new PropertyMetadata(null));

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        // The binding this comes from resolves when the DataContext arrives, which may be after the
        // grid was first given one. Whatever was wired up for the old section goes first.
        ((Attachment?)grid.GetValue(AttachmentProperty))?.Detach();
        grid.SetValue(AttachmentProperty, null);

        if (e.NewValue is not GridLayoutSection section) return;

        var attachment = new Attachment(grid, section);
        grid.SetValue(AttachmentProperty, attachment);
        attachment.Attach();
    }
}
