using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.Bindings.Interfaces;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>Moving rows around by dragging them: the condition tree, and the two lists of rules.</summary>
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
    // In-process only, so the payload is the row's own data rather than anything serialized.
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
        if (element.DataContext is not object row) return;

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

        object? source = Dragged(e);
        if (source is null)
        {
            ClearMark();
            return;
        }

        AutoScroll(surface, e);

        (FrameworkElement Zone, Landing Target, DropWhere Where)? target = Resolve(surface, e);
        if (target is null || !target.Value.Target.CanAccept(source, target.Value.Where))
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

        object? source = Dragged(e);
        if (source is null) return;

        (FrameworkElement Zone, Landing Target, DropWhere Where)? target = Resolve(surface, e);
        if (target is null || !target.Value.Target.CanAccept(source, target.Value.Where)) return;

        target.Value.Target.Accept(source, target.Value.Where);
        e.Effects = DragDropEffects.Move;
    }

    private static object? Dragged(DragEventArgs e)
        => e.Data.GetDataPresent(RowFormat) ? e.Data.GetData(RowFormat) : null;

    /// <summary>What is under the pointer, and which side of it the dragged row would go.</summary>
    private static (FrameworkElement Zone, Landing Target, DropWhere Where)? Resolve(
        FrameworkElement surface, DragEventArgs e)
    {
        HitTestResult? hit = VisualTreeHelper.HitTest(surface, e.GetPosition(surface));
        if (hit is null) return null;

        for (DependencyObject? at = hit.VisualHit; at != null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is not FrameworkElement element) continue;

            DropZoneKind kind = GetDropZone(element);
            if (kind == DropZoneKind.None) continue;
            if (LandingOn(element) is not { } landing) return null;

            double y = e.GetPosition(element).Y;
            DropWhere where = kind switch
            {
                DropZoneKind.Row => y < element.ActualHeight / 2 ? DropWhere.Before : DropWhere.After,
                DropZoneKind.GroupHeader => y < element.ActualHeight / 2 ? DropWhere.Before : DropWhere.Inside,
                DropZoneKind.GroupTail => DropWhere.After,
                _ => DropWhere.None,
            };

            return where == DropWhere.None ? null : (element, landing, where);
        }

        return null;
    }

    // A row either answers for itself or its list answers for it, and which of the two is a
    // property of the list rather than of the drag: a condition row knows the tree it is in, while
    // a saved rule is only ever a row somebody is drawing.
    private static Landing? LandingOn(FrameworkElement zone)
    {
        if (zone.DataContext is IDragRow row) return new Landing(row);

        object? item = zone.DataContext;
        if (item is null) return null;

        for (DependencyObject? at = zone; at != null; at = VisualTreeHelper.GetParent(at))
            if (GetDropList(at) is IDragList list) return new Landing(list, item);

        return null;
    }

    /// <summary>The row a drop would be handed to, whichever of the two ways it answers.</summary>
    private readonly struct Landing
    {
        private readonly IDragRow? _row;
        private readonly IDragList? _list;
        private readonly object? _item;

        public Landing(IDragRow row)
        {
            _row = row;
            _list = null;
            _item = null;
        }

        public Landing(IDragList list, object item)
        {
            _row = null;
            _list = list;
            _item = item;
        }

        public bool CanAccept(object dragged, DropWhere where)
            => _row?.CanAccept(dragged, where) ?? _list!.CanAccept(dragged, _item!, where);

        public void Accept(object dragged, DropWhere where)
        {
            if (_row != null) _row.Accept(dragged, where);
            else _list!.Accept(dragged, _item!, where);
        }
    }

    // A list long enough to need dragging is a list that has to be scrolled, and the pointer is
    // holding a row while it does — so the list has to come to it.
    private static void AutoScroll(FrameworkElement surface, DragEventArgs e)
    {
        ScrollViewer? scroller = surface as ScrollViewer ?? ScrollerIn(surface);
        if (scroller is null) return;

        double y = e.GetPosition(scroller).Y;
        if (y < EdgeMargin)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - EdgeStep);
        else if (y > scroller.ActualHeight - EdgeMargin)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + EdgeStep);
    }

    // Breadth first, and only as far as the first one: a grid keeps its scroller near the top of
    // its template and its rows underneath it, so this stops before it walks into the rows.
    private static ScrollViewer? ScrollerIn(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            DependencyObject at = queue.Dequeue();
            int count = VisualTreeHelper.GetChildrenCount(at);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(at, i);
                if (child is ScrollViewer scroller) return scroller;

                queue.Enqueue(child);
            }
        }

        return null;
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

    /// <summary>The list that answers drops for the rows under this element, if they cannot.</summary>
    public static readonly DependencyProperty DropListProperty =
        DependencyProperty.RegisterAttached(
            "DropList", typeof(IDragList), typeof(DragReorderBehavior),
            new PropertyMetadata(null));

    public static void SetDropList(DependencyObject element, IDragList? value)
        => element.SetValue(DropListProperty, value);

    public static IDragList? GetDropList(DependencyObject element)
        => (IDragList?)element.GetValue(DropListProperty);

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
