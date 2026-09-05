using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Models;
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

    // The list opens with the filter's own policies first, in its own order — not in the order the
    // configuration happens to store the policies in.
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
    }

    [Fact]
    public void Ticking_one_more_puts_it_last_until_it_is_moved()
    {
        var rule = Filter(Work.Id);
        var model = new ProcessFilterViewModel(rule, All);

        model.Policies.Single(p => p.Name == "Games").IsSelected = true;
        model.ApplyTo(rule);

        Assert.Equal(new[] { Work.Id, Games.Id }, rule.PolicyIds);
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
