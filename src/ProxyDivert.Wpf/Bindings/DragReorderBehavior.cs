using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.Bindings.Interfaces;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>Moving rows around by dragging them, for the condition tree and the policy list.</summary>
/// <remarks>
/// Written as one surface that hit-tests, rather than as handlers on every row. Rows are drawn by
/// templates nested inside each other, and drag events tunnel outside-in and bubble inside-out, so
/// per-row handlers make the outermost group answer for a point that is really over a row four
/// levels down. Hit-testing asks the opposite question — what is actually under the pointer — and
/// the nearest marked ancestor of that is the answer, whatever the nesting.
///
/// It also means the row templates carry no code: a marked element and a grip are all a list needs
/// to become re-orderable.
/// </remarks>
public static class DragReorderBehavior
{
    // In-process only, so the payload is the view model itself rather than anything serialized.
    private const string RowFormat = "ProxyDivert.DragRow";

    // How close to the edge of the list the pointer has to get before it starts scrolling, and how
    // far each drag event scrolls it. Small steps: this fires as fast as the mouse moves.
    private const double EdgeMargin = 26;
    private const double EdgeStep = 12;

    // There is one mouse, so there is one drag. Kept here rather than per element because the
    // press and the move that turns it into a drag arrive on the same grip, while the line being
    // drawn belongs to whatever the pointer has since moved over.
    private static Point _pressedAt;
    private static bool _pressed;
    private static FrameworkElement? _marked;

    // ==== the grip: what you pick a row up by ====

    public static readonly DependencyProperty IsDragHandleProperty =
        DependencyProperty.RegisterAttached(
            "IsDragHandle", typeof(bool), typeof(DragReorderBehavior),
            new PropertyMetadata(false, OnIsDragHandleChanged));

    public static void SetIsDragHandle(DependencyObject element, bool value)
        => element.SetValue(IsDragHandleProperty, value);

    public static bool GetIsDragHandle(DependencyObject element)
        => (bool)element.GetValue(IsDragHandleProperty);

    private static void OnIsDragHandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.PreviewMouseLeftButtonDown -= OnHandlePressed;
        element.PreviewMouseMove -= OnHandleMoved;
        element.PreviewMouseLeftButtonUp -= OnHandleReleased;

        if (e.NewValue is not true) return;

        element.PreviewMouseLeftButtonDown += OnHandlePressed;
        element.PreviewMouseMove += OnHandleMoved;
        element.PreviewMouseLeftButtonUp += OnHandleReleased;
    }

    private static void OnHandlePressed(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(null);
        _pressed = true;
    }

    private static void OnHandleReleased(object sender, MouseButtonEventArgs e) => _pressed = false;

    // The drag only begins once the pointer has actually travelled: starting one on the press
    // itself would turn every click on the grip into a drag nobody asked for.
    private static void OnHandleMoved(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement element) return;

        Vector moved = e.GetPosition(null) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressed = false;
        if (element.DataContext is not IDragRow row) return;

        var payload = new DataObject(RowFormat, row);

        // Blocks until the drop, so whatever the gesture left behind is cleared underneath it.
        DragDrop.DoDragDrop(element, payload, DragDropEffects.Move);
        ClearMark();
    }

    // ==== the surface: the one place that decides where a drop would go ====

    public static readonly DependencyProperty IsDropSurfaceProperty =
        DependencyProperty.RegisterAttached(
            "IsDropSurface", typeof(bool), typeof(DragReorderBehavior),
            new PropertyMetadata(false, OnIsDropSurfaceChanged));

    public static void SetIsDropSurface(DependencyObject element, bool value)
        => element.SetValue(IsDropSurfaceProperty, value);

    public static bool GetIsDropSurface(DependencyObject element)
        => (bool)element.GetValue(IsDropSurfaceProperty);

    private static void OnIsDropSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.PreviewDragOver -= OnSurfaceDragOver;
        element.PreviewDrop -= OnSurfaceDrop;
        element.PreviewDragLeave -= OnSurfaceDragLeave;

        if (e.NewValue is not true) return;

        element.AllowDrop = true;

        // Preview, not the bubbling pair: a row carries text boxes and combo boxes that answer
        // drag events themselves, and a condition row is not a place to drop text.
        element.PreviewDragOver += OnSurfaceDragOver;
        element.PreviewDrop += OnSurfaceDrop;
        element.PreviewDragLeave += OnSurfaceDragLeave;
    }

    private static void OnSurfaceDragLeave(object sender, DragEventArgs e) => ClearMark();

    private static void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement surface) return;

        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (Dragged(e) is not IDragRow source)
        {
            ClearMark();
            return;
        }

        AutoScroll(surface, e);

        (FrameworkElement Zone, IDragRow Row, DropWhere Where)? target = Resolve(surface, e);
        if (target is null || !target.Value.Row.CanAccept(source, target.Value.Where))
        {
            ClearMark();
            return;
        }

        Mark(target.Value.Zone, target.Value.Where);
        e.Effects = DragDropEffects.Move;
    }

    private static void OnSurfaceDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement surface) return;

        ClearMark();
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (Dragged(e) is not IDragRow source) return;

        (FrameworkElement Zone, IDragRow Row, DropWhere Where)? target = Resolve(surface, e);
        if (target is null || !target.Value.Row.CanAccept(source, target.Value.Where)) return;

        target.Value.Row.Accept(source, target.Value.Where);
        e.Effects = DragDropEffects.Move;
    }

    private static IDragRow? Dragged(DragEventArgs e)
        => e.Data.GetDataPresent(RowFormat) ? e.Data.GetData(RowFormat) as IDragRow : null;

    /// <summary>What is under the pointer, and which side of it the dragged row would go.</summary>
    private static (FrameworkElement Zone, IDragRow Row, DropWhere Where)? Resolve(
        FrameworkElement surface, DragEventArgs e)
    {
        HitTestResult? hit = VisualTreeHelper.HitTest(surface, e.GetPosition(surface));
        if (hit is null) return null;

        for (DependencyObject? at = hit.VisualHit; at != null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is not FrameworkElement element) continue;

            DropZoneKind kind = GetDropZone(element);
            if (kind == DropZoneKind.None) continue;
            if (element.DataContext is not IDragRow row) return null;

            double y = e.GetPosition(element).Y;
            DropWhere where = kind switch
            {
                DropZoneKind.Row => y < element.ActualHeight / 2 ? DropWhere.Before : DropWhere.After,
                DropZoneKind.GroupHeader => y < element.ActualHeight / 2 ? DropWhere.Before : DropWhere.Inside,
                DropZoneKind.GroupTail => DropWhere.After,
                _ => DropWhere.None,
            };

            return where == DropWhere.None ? null : (element, row, where);
        }

        return null;
    }

    // A tree deep enough to need dragging is a tree that has to be scrolled, and the pointer is
    // holding a row while it does — so the list has to come to it.
    private static void AutoScroll(FrameworkElement surface, DragEventArgs e)
    {
        if (surface is not ScrollViewer scroller) return;

        double y = e.GetPosition(scroller).Y;
        if (y < EdgeMargin)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - EdgeStep);
        else if (y > scroller.ActualHeight - EdgeMargin)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + EdgeStep);
    }

    // ==== where the line is drawn ====

    /// <summary>Marks an element as somewhere a row may be dropped. Read by the hit test.</summary>
    public static readonly DependencyProperty DropZoneProperty =
        DependencyProperty.RegisterAttached(
            "DropZone", typeof(DropZoneKind), typeof(DragReorderBehavior),
            new PropertyMetadata(DropZoneKind.None));

    public static void SetDropZone(DependencyObject element, DropZoneKind value)
        => element.SetValue(DropZoneProperty, value);

    public static DropZoneKind GetDropZone(DependencyObject element)
        => (DropZoneKind)element.GetValue(DropZoneProperty);

    /// <summary>Where the insertion line is showing on this zone, if it is. Read by the template.</summary>
    public static readonly DependencyProperty DropMarkProperty =
        DependencyProperty.RegisterAttached(
            "DropMark", typeof(DropWhere), typeof(DragReorderBehavior),
            new PropertyMetadata(DropWhere.None));

    public static void SetDropMark(DependencyObject element, DropWhere value)
        => element.SetValue(DropMarkProperty, value);

    public static DropWhere GetDropMark(DependencyObject element)
        => (DropWhere)element.GetValue(DropMarkProperty);

    private static void Mark(FrameworkElement element, DropWhere where)
    {
        if (!ReferenceEquals(_marked, element)) ClearMark();

        _marked = element;
        SetDropMark(element, where);
    }

    private static void ClearMark()
    {
        if (_marked is null) return;

        SetDropMark(_marked, DropWhere.None);
        _marked = null;
    }
}
