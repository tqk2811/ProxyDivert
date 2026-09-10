using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Bindings;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.Bindings.Interfaces;
using ProxyDivert.Wpf.Services;
using ProxyDivert.Wpf.ViewModels;
using ProxyDivert.Wpf.Views;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A process is caught by the FIRST filter that matches it, so the order of the filter grid decides
// which one runs — and the grid is the only place that order can be set. These cover the two ways
// that can silently be wrong: the row not answering the drop at all, and the row moving on screen
// while the list the engine reads stays as it was.
public class ProcessRuleDragTests
{
    // A filter is the saved model and knows nothing about windows, so the view model answers drops
    // on its behalf. That hand-off runs through a binding, and a binding that resolves to nothing
    // fails in silence: the rows simply refuse every drop, with nothing logged.
    [Collection("WPF")]
    public class TheGrid
    {
        [Fact]
        public void A_filter_row_hands_its_drop_to_the_list_that_draws_it()
        {
            DropZoneKind kind = DropZoneKind.None;
            object? droppedOn = null;
            object? handler = null;
            int grips = 0;

            WpfHost.RunOnStaThread(() =>
            {
                WpfHost.EnsureApplication();

                var stub = new ListStub();
                stub.Rules.Add(Filter("one.exe"));
                stub.Rules.Add(Filter("two.exe"));

                var view = new ProcessesView { DataContext = stub };
                var window = new Window { Width = 1200, Height = 800, Content = view };

                window.Show();
                view.UpdateLayout();

                DataGrid grid = WpfHost.Descendants<DataGrid>(view).First();
                DataGridRow second = WpfHost.Descendants<DataGridRow>(grid)
                    .First(row => ReferenceEquals(row.DataContext, stub.Rules[1]));

                grips = WpfHost.Descendants<FrameworkElement>(grid)
                    .Count(element => DragReorderBehavior.GetIsDragHandle(element));

                // Over a cell, which is what the pointer is actually over on a grid: the row it
                // belongs to has to be the thing that answers.
                DataGridCell cell = WpfHost.Descendants<DataGridCell>(second).First();
                Point point = cell.TransformToAncestor(grid)
                    .Transform(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2));

                (kind, droppedOn, handler) = ZoneUnder(grid, point);

                window.Close();
            }, "The rules grid");

            Assert.Equal(DropZoneKind.Row, kind);
            Assert.IsType<ProcessRule>(droppedOn);
            Assert.IsType<ListStub>(handler);
            Assert.Equal(2, grips);
        }

        // Everything ProcessesView binds to, plus the one thing this is about: the list answering
        // for its rows.
        private sealed class ListStub : IDragList
        {
            public ObservableCollection<ProcessRule> Rules { get; } = new ObservableCollection<ProcessRule>();

            public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

            public ObservableCollection<object> AppliedProcesses { get; } = new ObservableCollection<object>();

            public ProcessRule? SelectedRule { get; set; }

            public object? SelectedProcess { get; set; }

            public bool CanAccept(object dragged, object target, DropWhere where) => true;

            public void Accept(object dragged, object target, DropWhere where)
            {
            }
        }

        // The walk the drag itself does: what is under the pointer, the nearest marked ancestor of
        // it, and who answers for that row.
        private static (DropZoneKind Kind, object? Row, object? Handler) ZoneUnder(Visual surface, Point point)
        {
            HitTestResult? hit = VisualTreeHelper.HitTest(surface, point);
            if (hit is null) return (DropZoneKind.None, null, null);

            for (DependencyObject? at = hit.VisualHit; at != null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is not FrameworkElement element) continue;
                if (DragReorderBehavior.GetDropZone(element) == DropZoneKind.None) continue;

                for (DependencyObject? owner = element; owner != null; owner = VisualTreeHelper.GetParent(owner))
                    if (DragReorderBehavior.GetDropList(owner) is IDragList list)
                        return (DropZoneKind.Row, element.DataContext, list);

                return (DropZoneKind.Row, element.DataContext, null);
            }

            return (DropZoneKind.None, null, null);
        }
    }

    // The grid is a copy of the saved list. Moving a row in the copy alone leaves the window
    // looking exactly right while the engine goes on matching in the old order — the kind of wrong
    // that only shows up as traffic taking a route nobody chose.
    [Fact]
    public async Task Dragging_a_filter_rewrites_the_order_the_engine_matches_in()
    {
        string path = Path.Combine(Path.GetTempPath(), $"proxydivert-drag-{Guid.NewGuid():N}.json");

        try
        {
            await using var services = new AppServices(path);

            services.Config.ProcessRules.Add(Filter("one.exe"));
            services.Config.ProcessRules.Add(Filter("two.exe"));
            services.Config.ProcessRules.Add(Filter("three.exe"));

            var model = new ProcessesViewModel(services);
            ProcessFilterRowViewModel first = model.Rules[0];

            Assert.True(model.CanAccept(first, model.Rules[2], DropWhere.After));
            model.Accept(first, model.Rules[2], DropWhere.After);

            Assert.Equal(
                new[] { "two.exe", "three.exe", "one.exe" },
                model.Rules.Select(rule => rule.Name));

            Assert.Equal(
                new[] { "two.exe", "three.exe", "one.exe" },
                services.Config.ProcessRules.Select(rule => rule.Name));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // A row let go where it already is has not been arranged, and a filter cannot be dropped on
    // itself at all.
    [Fact]
    public async Task A_filter_dropped_where_it_already_is_moves_nothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"proxydivert-drag-{Guid.NewGuid():N}.json");

        try
        {
            await using var services = new AppServices(path);

            services.Config.ProcessRules.Add(Filter("one.exe"));
            services.Config.ProcessRules.Add(Filter("two.exe"));

            var model = new ProcessesViewModel(services);
            ProcessFilterRowViewModel first = model.Rules[0];

            Assert.False(model.CanAccept(first, first, DropWhere.After));

            model.Accept(first, model.Rules[1], DropWhere.Before);

            Assert.Equal(new[] { "one.exe", "two.exe" }, model.Rules.Select(rule => rule.Name));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static ProcessRule Filter(string pattern)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = pattern,
            Condition = new ConditionGroup
            {
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = pattern },
                },
            },
        };
}
