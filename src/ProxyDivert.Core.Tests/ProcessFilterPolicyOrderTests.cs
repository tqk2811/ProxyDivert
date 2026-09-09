using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.ViewModels;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A filter names several policies, and the order it names them in is the priority the resolver
// walks. The editor is where that order is arranged, so this is where it can go wrong without
// anything failing to build: a list that comes back in a different order than it was ticked in
// routes traffic through the wrong rule set, and the only symptom is a connection going somewhere
// the user did not choose.
public class ProcessFilterPolicyOrderTests
{
    private static readonly RoutingPolicy Work = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Work" };
    private static readonly RoutingPolicy Streaming = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Streaming" };
    private static readonly RoutingPolicy Games = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Games" };

    private static RoutingPolicy[] All => new[] { Work, Streaming, Games };

    private static ProcessRule Filter(params Guid[] policyIds)
        => new ProcessRule { Id = Guid.NewGuid(), Name = "test", PolicyIds = policyIds.ToList() };

    // A filter saved before the arrangement was kept: it names its policies and nothing more.
    // The list opens with those first, in its own order — not in the order the configuration
    // happens to store the policies in.
    [Fact]
    public void The_chosen_policies_come_first_in_the_order_the_filter_named_them()
    {
        var model = new ProcessFilterViewModel(Filter(Games.Id, Work.Id), All);

        Assert.Equal(
            new[] { "Games", "Work", "Streaming" },
            model.Policies.Select(p => p.Name));

        Assert.Equal(new[] { true, true, false }, model.Policies.Select(p => p.IsSelected));

        // Numbered where the number means something, and nowhere else.
        Assert.Equal(new[] { 1, 2, 0 }, model.Policies.Select(p => p.Rank));

        Assert.Equal("Games → Work", model.PolicySummary);
    }

    [Fact]
    public void Moving_a_policy_changes_what_the_filter_is_saved_with()
    {
        var rule = Filter(Games.Id, Work.Id);
        var model = new ProcessFilterViewModel(rule, All);

        model.MovePolicyDownCommand.Execute(model.Policies[0]);
        model.ApplyTo(rule);

        Assert.Equal(new[] { Work.Id, Games.Id }, rule.PolicyIds);
        Assert.Equal("Work → Games", model.PolicySummary);

        // The whole arrangement is saved, not only the ticked part of it.
        Assert.Equal(new[] { Work.Id, Games.Id, Streaming.Id }, rule.PolicyOrder);
    }

    // Dragging is the other way to arrange the list, and it asks a different question than the
    // arrows do: which gap to fill, rather than how many places to step.
    [Fact]
    public void Dropping_a_policy_below_another_puts_it_there()
    {
        var rule = Filter(Work.Id, Streaming.Id, Games.Id);
        var model = new ProcessFilterViewModel(rule, All);

        ProcessFilterViewModel.PolicyChoice work = model.Policies[0];
        model.Policies[2].Accept(work, DropWhere.After);
        model.ApplyTo(rule);

        Assert.Equal(new[] { "Streaming", "Games", "Work" }, model.Policies.Select(p => p.Name));
        Assert.Equal(new[] { Streaming.Id, Games.Id, Work.Id }, rule.PolicyIds);
        Assert.Equal(new[] { 1, 2, 3 }, model.Policies.Select(p => p.Rank));
    }

    // A row let go where it already is has not been arranged, and answering the close button with
    // "you have unsaved changes" over a drag that moved nothing is how that question stops meaning
    // anything.
    [Fact]
    public void A_policy_dropped_where_it_already_is_is_not_an_edit()
    {
        var model = new ProcessFilterViewModel(Filter(Work.Id), All);
        ProcessFilterViewModel.PolicyChoice work = model.Policies[0];

        Assert.False(work.CanAccept(work, DropWhere.After));
        work.Accept(work, DropWhere.After);

        // Straight back into the gap above the row under it, which is the one it fills already.
        model.Policies[1].Accept(work, DropWhere.Before);

        Assert.False(model.IsDirty);
        Assert.Equal(new[] { "Work", "Streaming", "Games" }, model.Policies.Select(p => p.Name));
    }

    [Fact]
    public void Ticking_one_more_gives_it_the_place_it_is_already_standing_in()
    {
        var rule = Filter(Work.Id);
        var model = new ProcessFilterViewModel(rule, All);

        model.Policies.Single(p => p.Name == "Games").IsSelected = true;
        model.ApplyTo(rule);

        Assert.Equal(new[] { Work.Id, Games.Id }, rule.PolicyIds);
    }

    // The one the user complained about: unticking a policy used to throw its place away, because
    // the window rebuilt the list ticked-first every time it opened. A row put back then came up
    // somewhere else than where it was left, and so did every row under it.
    [Fact]
    public void Unticking_a_policy_leaves_it_where_it_was()
    {
        var rule = Filter(Work.Id, Streaming.Id, Games.Id);
        var model = new ProcessFilterViewModel(rule, All);

        model.Policies.Single(p => p.Name == "Work").IsSelected = false;
        model.ApplyTo(rule);

        Assert.Equal(new[] { Streaming.Id, Games.Id }, rule.PolicyIds);

        var reopened = new ProcessFilterViewModel(rule, All);

        Assert.Equal(
            new[] { "Work", "Streaming", "Games" },
            reopened.Policies.Select(p => p.Name));
        Assert.Equal(new[] { false, true, true }, reopened.Policies.Select(p => p.IsSelected));
        Assert.Equal(new[] { 0, 1, 2 }, reopened.Policies.Select(p => p.Rank));
    }

    // Arranging and ticking are two different things, and the arrangement is the one that has to
    // survive: a policy moved to the top while unticked is at the top when it is ticked later.
    [Fact]
    public void An_unticked_policy_keeps_the_place_it_was_moved_to()
    {
        var rule = Filter(Work.Id);
        var model = new ProcessFilterViewModel(rule, All);

        model.MovePolicyUpCommand.Execute(model.Policies.Single(p => p.Name == "Games"));
        model.ApplyTo(rule);

        var reopened = new ProcessFilterViewModel(rule, All);

        Assert.Equal(
            new[] { "Work", "Games", "Streaming" },
            reopened.Policies.Select(p => p.Name));

        reopened.Policies.Single(p => p.Name == "Games").IsSelected = true;
        reopened.ApplyTo(rule);

        Assert.Equal(new[] { Work.Id, Games.Id }, rule.PolicyIds);
    }

    // A policy created after the filter was last saved is not in its arrangement, so it goes at
    // the end rather than nowhere.
    [Fact]
    public void A_policy_the_arrangement_never_saw_is_listed_last()
    {
        var rule = Filter(Games.Id);
        var model = new ProcessFilterViewModel(rule, new[] { Work, Games });
        model.ApplyTo(rule);

        var reopened = new ProcessFilterViewModel(rule, All);

        Assert.Equal(
            new[] { "Games", "Work", "Streaming" },
            reopened.Policies.Select(p => p.Name));
    }

    // Nothing ticked would leave the filter catching processes with no rules to route them by, and
    // they would go out direct — the one outcome a redirector must never produce by accident.
    [Fact]
    public void A_filter_with_nothing_ticked_still_gets_a_policy()
    {
        var rule = Filter(Streaming.Id);
        var model = new ProcessFilterViewModel(rule, All);

        foreach (ProcessFilterViewModel.PolicyChoice choice in model.Policies) choice.IsSelected = false;
        model.ApplyTo(rule);

        Assert.Equal(new[] { Streaming.Id }, rule.PolicyIds);
    }

    // The list holds every policy so one can be ticked without going to a second list, and a policy
    // the filter names that has since been deleted simply is not in it.
    [Fact]
    public void A_policy_the_filter_names_but_that_no_longer_exists_is_dropped()
    {
        var rule = Filter(Guid.NewGuid(), Work.Id);
        var model = new ProcessFilterViewModel(rule, All);
        model.ApplyTo(rule);

        Assert.Equal(All.Length, model.Policies.Count);
        Assert.Equal(new[] { Work.Id }, rule.PolicyIds);
    }
}
