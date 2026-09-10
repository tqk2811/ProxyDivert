using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Models;
using System.Windows;
using System.Windows.Controls;
using ProxyDivert.Wpf.ViewModels;
using ProxyDivert.Wpf.Views;
using Xunit;
using Node = ProxyDivert.Wpf.ViewModels.ProcessesViewModel.AppliedProcessNode;

namespace ProxyDivert.Core.Tests;

// The "processes being redirected" tree says which filter caught each process. The tracker only
// records that on the process that actually matched one — a child adopted through IncludeChildren
// carries no filter of its own — so the answer for most rows in a real tree is arrived at by
// walking up the parents. Getting that wrong shows an empty column next to a process that is very
// much being redirected, and there is nothing else in the window to ask.
public class AppliedProcessTreeTests
{
    private static readonly ProcessRule Games = new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = "Games",
        PolicyIds = { Guid.NewGuid() },
        IncludeChildren = true,
    };

    private static TrackedProcess Matched(uint pid, string name, uint parent = 0)
        => new TrackedProcess(pid, name, null, Games, Games.PolicyIds, parentProcessId: parent);

    private static TrackedProcess Adopted(uint pid, string name, uint parent)
        => new TrackedProcess(pid, name, null, null, Games.PolicyIds, parentProcessId: parent);

    [Fact]
    public void A_process_shows_the_filter_that_caught_it()
    {
        List<Node> roots = ProcessesViewModel.BuildTree(new[] { Matched(100, "steam.exe") });

        Assert.Equal("Games", Assert.Single(roots).Filter);
    }

    // The one the walk exists for: neither the child nor the grandchild matched anything, they were
    // dragged in by the process above them, and the filter to go and edit is that one.
    [Fact]
    public void An_adopted_child_shows_the_filter_that_caught_its_parent()
    {
        List<Node> roots = ProcessesViewModel.BuildTree(new[]
        {
            Matched(100, "steam.exe"),
            Adopted(200, "helper.exe", parent: 100),
            Adopted(300, "deeper.exe", parent: 200),
        });

        Node root = Assert.Single(roots);
        Node child = Assert.Single(root.Children);
        Node grandchild = Assert.Single(child.Children);

        Assert.Equal("Games", child.Filter);
        Assert.Equal("Games", grandchild.Filter);
    }

    // A child whose parent has already exited stands on its own — it is still being redirected —
    // and then there is no chain left to walk. Better an em dash than a name that is not true.
    [Fact]
    public void A_child_whose_parent_is_gone_stands_alone_with_nothing_to_name()
    {
        List<Node> roots = ProcessesViewModel.BuildTree(new[] { Adopted(200, "helper.exe", parent: 100) });

        Assert.Equal("—", Assert.Single(roots).Filter);
    }

    // --pid and Launch suspended name a process id outright. No filter describes it, and saying one
    // does would send the user to a filter that has nothing to do with it.
    [Fact]
    public void A_process_the_caller_named_itself_shows_no_filter()
    {
        var launched = new TrackedProcess(
            100, "game.exe", null, matchedRule: null, policyIds: Games.PolicyIds, isExplicit: true);

        List<Node> roots = ProcessesViewModel.BuildTree(new[] { launched });

        Assert.Equal("—", Assert.Single(roots).Filter);
    }

    // Process ids are reused, so a parent id can point at a process further down its own branch.
    // The walk is bounded for exactly this: an unbounded one would hang the window while it drew a
    // list, with nothing to say why.
    [Fact]
    public void A_parent_chain_that_points_back_at_itself_does_not_hang()
    {
        List<Node> roots = ProcessesViewModel.BuildTree(new[]
        {
            Adopted(100, "one.exe", parent: 200),
            Adopted(200, "two.exe", parent: 100),
        });

        // Each claims the other as its parent, so neither is a root and the tree draws nothing.
        // What matters is that the answer arrives at all.
        Assert.Empty(roots);
    }

    [Fact]
    public void Rows_stand_in_name_order_with_children_under_their_parent()
    {
        List<Node> roots = ProcessesViewModel.BuildTree(new[]
        {
            Matched(100, "steam.exe"),
            Matched(50, "chrome.exe"),
            Adopted(200, "steamwebhelper.exe", parent: 100),
        });

        Assert.Equal(new[] { "chrome.exe", "steam.exe" }, roots.Select(node => node.Name));
        Assert.Equal("steamwebhelper.exe", Assert.Single(roots[1].Children).Name);
    }

    // The argument column shows the command line without its first token: Windows puts the
    // executable at the front of it, the column beside this one already shows that path, and
    // repeating it pushed the flags — the only part worth reading — out past the trim.
    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --port 8080", "--port 8080")]
    [InlineData(@"C:\app\app.exe --port 8080", "--port 8080")]
    [InlineData(@"""C:\Program Files\App\app.exe""", "")]
    [InlineData("app.exe", "")]
    [InlineData("   app.exe  --flag  ", "--flag")]
    public void The_argument_column_drops_the_executable_the_path_column_already_shows(
        string commandLine, string expected)
    {
        Assert.Equal(expected, Node.SplitArguments(commandLine));
    }

    // Null is not the same as no arguments: the command line could not be read at all — a process
    // that exited between the listing and the read, or one owned by another user — and an empty
    // cell claiming "started with nothing" would be a statement this code cannot stand behind.
    [Fact]
    public void A_command_line_that_could_not_be_read_stays_unknown()
    {
        Assert.Null(Node.SplitArguments(null));
    }

    // A tree has no columns. The headings and the cells under them are separate elements that only
    // line up because both read the same numbers off the view model, and the splitter in the
    // heading writes back to them. A property renamed on one side of that and not the other leaves
    // the columns at their natural width — nothing fails, the tab simply stops lining up.
    [Collection("WPF")]
    public class TheColumns
    {
        [Fact]
        public void The_headings_take_their_width_from_the_view_model()
        {
            double first = 0;
            double second = 0;
            int splitters = 0;

            WpfHost.RunOnStaThread(
                () =>
                {
                    WpfHost.EnsureApplication();

                    var view = new ProcessesView { DataContext = new WidthStub() };
                    var window = new Window { Width = 1400, Height = 900, Content = view };

                    window.Show();
                    view.UpdateLayout();

                    Grid headings = WpfHost.Descendants<Grid>(view)
                        .First(grid => grid.ColumnDefinitions.Count == 5
                                       && WpfHost.Descendants<GridSplitter>(grid).Any());

                    first = headings.ColumnDefinitions[0].ActualWidth;
                    second = headings.ColumnDefinitions[1].ActualWidth;
                    splitters = WpfHost.Descendants<GridSplitter>(headings).Count();

                    window.Close();
                },
                "The redirected-process headings");

            Assert.Equal(123, first);
            Assert.Equal(234, second);

            // One on every column that can be widened, which is all of them but the last: the last
            // takes whatever is left over.
            Assert.Equal(4, splitters);
        }

        // Only what the headings read. The rest of the view binds to nothing here, which costs a
        // few binding errors in the log and nothing else.
        //
        // Settable, and not because this test writes to them: the headings bind TwoWay so that the
        // splitter can hand the new width back, and WPF refuses such a binding on a read-only
        // property outright.
        private sealed class WidthStub
        {
            public double AppliedPidWidth { get; set; } = 123;

            public double AppliedNameWidth { get; set; } = 234;

            public double AppliedFilterWidth { get; set; } = 210;

            public double AppliedPathWidth { get; set; } = 300;

            public ObservableCollection<Node> AppliedProcesses { get; } = new ObservableCollection<Node>();
        }
    }
}
