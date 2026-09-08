using System;
using System.Linq;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.ViewModels;
using ProxyDivert.Wpf.ViewModels.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Closing the filter window asks whether to save, and it asks only when there is something to
// lose. Both halves of that matter: a question nobody needs teaches the user to dismiss it without
// reading, and a missing question throws an edit away without a word.
public class ProcessFilterDirtyTests
{
    private static readonly RoutingPolicy Work = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Work" };
    private static readonly RoutingPolicy Games = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Games" };

    private static RoutingPolicy[] All => new[] { Work, Games };

    private static ProcessFilterViewModel Open()
        => new ProcessFilterViewModel(
            new ProcessRule { Id = Guid.NewGuid(), Name = "test", PolicyIds = { Work.Id } },
            All);

    // Building the list numbers the ticked rows and writes the summary line, and none of that is
    // the user editing anything.
    [Fact]
    public void Opening_a_filter_is_not_an_edit()
    {
        Assert.False(Open().IsDirty);
    }

    [Fact]
    public void Renaming_is_an_edit()
    {
        ProcessFilterViewModel model = Open();
        model.Name = "chrome";

        Assert.True(model.IsDirty);
    }

    [Fact]
    public void Following_child_processes_is_an_edit()
    {
        ProcessFilterViewModel model = Open();
        model.IncludeChildren = !model.IncludeChildren;

        Assert.True(model.IsDirty);
    }

    [Fact]
    public void Ticking_a_policy_is_an_edit()
    {
        ProcessFilterViewModel model = Open();
        model.Policies.Single(p => p.Name == "Games").IsSelected = true;

        Assert.True(model.IsDirty);
    }

    [Fact]
    public void Moving_a_policy_is_an_edit()
    {
        ProcessFilterViewModel model = Open();
        model.MovePolicyDownCommand.Execute(model.Policies[0]);

        Assert.True(model.IsDirty);
    }

    [Fact]
    public void Adding_a_condition_is_an_edit()
    {
        ProcessFilterViewModel model = Open();
        model.Root.AddConditionCommand.Execute(null);

        Assert.True(model.IsDirty);
    }

    // The tick box on a condition row is how you pick rows to put in a group together. It is never
    // saved, and the filter reads exactly the same with it on or off — so opening a filter, ticking
    // a row and closing must not be answered with "you have unsaved changes".
    [Fact]
    public void Ticking_a_condition_row_to_group_it_is_not_an_edit()
    {
        ProcessFilterViewModel model = Open();
        ConditionNodeViewModel row = model.Root.Children[0];

        row.IsSelected = true;
        row.IsSelected = false;

        Assert.False(model.IsDirty);
    }

    // Same for the flag that tells the window where to put the caret: the view clears it as soon
    // as it has, which is not the user editing anything either.
    [Fact]
    public void The_caret_moving_to_a_new_row_is_not_an_edit()
    {
        ProcessFilterViewModel model = Open();
        model.Root.AddConditionCommand.Execute(null);
        model.IsDirty = false;

        var added = (ConditionLeafViewModel)model.Root.Children.Last();
        added.IsNew = false;

        Assert.False(model.IsDirty);
    }
}
