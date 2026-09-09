using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Bindings;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.ViewModels;
using ProxyDivert.Wpf.ViewModels.Conditions;
using ProxyDivert.Wpf.Views;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Rows are moved by dragging them, and the whole gesture rests on one thing the compiler cannot
// check: that the point under the pointer resolves to the row it is visibly over. A condition row
// is mostly combo boxes and a text box, each of which answers drag events itself, and the rows are
// drawn by templates nested inside each other — so the window is opened here and actually
// hit-tested, because a zone that is transparent to the mouse looks exactly right until you drag.
[Collection("WPF")]
public class ConditionDragDropTests
{
    // The text box fills the middle of every condition row. If a drop were worked out by letting
    // events bubble up from whatever they landed on, this is the point that would be answered by
    // the text box — dropping a row would be dropping text into a filter pattern.
    [Fact]
    public void The_point_over_a_row_resolves_to_that_row_rather_than_to_the_box_under_the_pointer()
    {
        DropZoneKind kind = DropZoneKind.None;
        object? hitContext = null;
        object? expected = null;

        RunOnStaThread(() =>
        {
            using Editor editor = Editor.Open();

            ConditionNodeViewModel leaf = editor.Model.Root.Children[0];
            expected = leaf;

            FrameworkElement row = editor.ZoneOf(leaf, DropZoneKind.Row);
            TextBox pattern = Descendants<TextBox>(row).First();

            (kind, hitContext) = editor.ZoneUnder(Centre(pattern, editor.Surface));
        });

        Assert.Equal(DropZoneKind.Row, kind);
        Assert.Same(expected, hitContext);
    }

    // The strip under a group's last row is the only way to say "after this whole bracket", and it
    // is drawn by the group's own border rather than by anything inside it. Padding of nothing
    // there would leave the strip too thin to hit, which no test of the view models would notice.
    [Fact]
    public void The_strip_under_a_group_resolves_to_that_group()
    {
        DropZoneKind kind = DropZoneKind.None;
        object? hitContext = null;
        object? expected = null;

        RunOnStaThread(() =>
        {
            using Editor editor = Editor.Open();

            ConditionGroupViewModel inner = editor.Model.Root.Children
                .OfType<ConditionGroupViewModel>().Single();
            expected = inner;

            FrameworkElement bracket = editor.ZoneOf(inner, DropZoneKind.GroupTail);
            var strip = new Point(bracket.ActualWidth / 2, bracket.ActualHeight - 2);

            (kind, hitContext) = editor.ZoneUnder(bracket.TransformToAncestor(editor.Surface).Transform(strip));
        });

        Assert.Equal(DropZoneKind.GroupTail, kind);
        Assert.Same(expected, hitContext);
    }

    // Every row can be picked up except the outermost group, which is the tree itself and has
    // nowhere to go. A grip drawn on it would be a gesture that silently does nothing.
    [Fact]
    public void Every_row_but_the_outermost_group_has_a_grip()
    {
        int grips = 0;
        int rows = 0;
        bool rootHasOne = false;

        RunOnStaThread(() =>
        {
            using Editor editor = Editor.Open();

            grips = Descendants<FrameworkElement>(editor.Surface)
                .Count(element => DragReorderBehavior.GetIsDragHandle(element)
                                  && element.IsVisible);

            // Two conditions at the top level, the bracket, and the two conditions inside it.
            rows = Descendants<FrameworkElement>(editor.Surface)
                .Count(element => DragReorderBehavior.GetDropZone(element) is DropZoneKind.Row
                                                                           or DropZoneKind.GroupHeader);

            rootHasOne = Descendants<FrameworkElement>(editor.Surface)
                .Any(element => DragReorderBehavior.GetIsDragHandle(element)
                                && element.IsVisible
                                && ReferenceEquals(element.DataContext, editor.Model.Root));
        });

        Assert.Equal(rows - 1, grips);
        Assert.False(rootHasOne, "The outermost group offers a grip that cannot lead anywhere.");
    }

    // ==== the window under test ====

    private sealed class Editor : IDisposable
    {
        private readonly ProcessFilterWindow _window;

        private Editor(ProcessFilterWindow window, ProcessFilterViewModel model)
        {
            _window = window;
            Model = model;
        }

        public ProcessFilterViewModel Model { get; }

        /// <summary>The scroller the drop is worked out on: every zone is somewhere under it.</summary>
        public ScrollViewer Surface { get; private set; } = null!;

        public static Editor Open()
        {
            EnsureApplication();

            var policy = new RoutingPolicy
            {
                Id = Guid.NewGuid(),
                Name = "policy",
                OutboundId = Guid.NewGuid(),
            };

            var model = new ProcessFilterViewModel(SampleFilter(), new[] { policy });
            var window = new ProcessFilterWindow(model) { Width = 1000, Height = 800 };

            window.Show();
            window.UpdateLayout();

            var editor = new Editor(window, model)
            {
                Surface = Descendants<ScrollViewer>(window)
                    .First(DragReorderBehavior.GetIsDropSurface),
            };

            return editor;
        }

        public void Dispose() => _window.Close();

        /// <summary>The element that stands for one row, as the drag sees it.</summary>
        public FrameworkElement ZoneOf(object row, DropZoneKind kind)
            => Descendants<FrameworkElement>(Surface)
                .First(element => DragReorderBehavior.GetDropZone(element) == kind
                                  && ReferenceEquals(element.DataContext, row));

        /// <summary>What a drop at this point would be worked out against, the way the drag does it.</summary>
        public (DropZoneKind Kind, object? Row) ZoneUnder(Point point)
        {
            HitTestResult? hit = VisualTreeHelper.HitTest(Surface, point);
            if (hit is null) return (DropZoneKind.None, null);

            for (DependencyObject? at = hit.VisualHit; at != null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is not FrameworkElement element) continue;

                DropZoneKind kind = DragReorderBehavior.GetDropZone(element);
                if (kind != DropZoneKind.None) return (kind, element.DataContext);
            }

            return (DropZoneKind.None, null);
        }
    }

    private static Point Centre(FrameworkElement element, Visual within)
        => element.TransformToAncestor(within)
            .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

    private static ProcessRule SampleFilter()
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "Minecraft",
            PolicyIds = { Guid.NewGuid() },
            Condition = new ConditionGroup
            {
                Operator = ConditionOperator.All,
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "java.exe" },
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;

            foreach (T deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // One Application per process, and the tests that build views share it; see the WPF collection.
    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            var application = new ProxyDivert.Wpf.App();
            application.InitializeComponent();
        }

        if (Application.Current.CheckAccess())
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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

        if (failure != null) throw new Xunit.Sdk.XunitException($"The filter editor failed to answer: {failure}");
    }
}
