using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What the tracker owes the engine: the right processes attached, a filter edit that reaches an
// already-redirected process WITHOUT detaching it, and children that follow their parent.
//
// The process table underneath is fed by a fake machine, so these say nothing about WMI — and
// nothing here goes near the operating system, which is the point of the split: every question the
// tracker asks is answered out of memory.
public class ProcessRuleTrackerTests
{
    private static readonly Guid DirectPolicy = Guid.NewGuid();
    private static readonly Guid ProxyPolicy = Guid.NewGuid();

    private sealed class Fixture : IDisposable
    {
        public FakeProcessMachine Machine { get; } = new FakeProcessMachine();
        public ProcessInventory Inventory { get; }
        public ProcessRuleTracker Tracker { get; }
        public List<TrackedProcess> Attached { get; } = new List<TrackedProcess>();
        public List<TrackedProcess> Detached { get; } = new List<TrackedProcess>();

        public Fixture()
        {
            Inventory = new ProcessInventory(NullLogger<ProcessInventory>.Instance, Machine, Machine);
            Tracker = new ProcessRuleTracker(NullLogger<ProcessRuleTracker>.Instance, Inventory);
            Tracker.ProcessAttached += p => Attached.Add(p);
            Tracker.ProcessDetached += p => Detached.Add(p);
        }

        /// <summary>Brings the table up to date and matches the filters against it.</summary>
        public void Settle()
        {
            Inventory.Refresh();
            Tracker.MatchEverything();
        }

        public void Dispose()
        {
            Tracker.Dispose();
            Inventory.Dispose();
        }
    }

    private static ProcessRule Rule(string pattern, params Guid[] policies)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = pattern,
            IncludeChildren = true,
            Condition = new ConditionGroup
            {
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = pattern },
                },
            },
            PolicyIds = policies.ToList(),
        };

    private static ProcessRule ArgumentRule(string exe, string argument, params Guid[] policies)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = exe,
            IncludeChildren = true,
            Condition = new ConditionGroup
            {
                Operator = ConditionOperator.All,
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = exe },
                    new CommandLineCondition { Matcher = ArgumentMatcherType.Contains, Pattern = argument },
                },
            },
            PolicyIds = policies.ToList(),
        };

    [Fact]
    public void A_process_already_running_when_the_engine_starts_is_attached()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "chrome.exe");
        fixture.Inventory.Refresh();

        fixture.Tracker.Start(new[] { Rule("chrome.exe", DirectPolicy) });

        TrackedProcess tracked = Assert.Single(fixture.Attached);
        Assert.Equal(100u, tracked.ProcessId);
        Assert.Equal(new[] { DirectPolicy }, tracked.PolicyIds);
    }

    [Fact]
    public void A_process_that_starts_afterwards_is_attached_from_the_tables_event()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Start(new[] { Rule("chrome.exe", DirectPolicy) });

        fixture.Machine.Start(100, "chrome.exe");
        fixture.Inventory.Refresh();

        Assert.Equal(new uint[] { 100 }, fixture.Attached.Select(p => p.ProcessId));
    }

    [Fact]
    public void A_process_that_exits_is_detached_from_the_tables_event()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "chrome.exe").Start(4, "System");
        fixture.Tracker.Start(new[] { Rule("chrome.exe", DirectPolicy) });
        fixture.Settle();

        fixture.Machine.Exit(100);
        fixture.Inventory.Refresh();

        Assert.Equal(new uint[] { 100 }, fixture.Detached.Select(p => p.ProcessId));
    }

    [Fact]
    public void The_tool_refuses_to_redirect_itself()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Tracker.AttachProcessId((uint)Environment.ProcessId, DirectPolicy));
    }

    [Fact]
    public void The_vpn_helper_is_never_redirected_however_broad_the_filter_is()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "wireproxy.exe").Start(200, "chrome.exe");
        fixture.Inventory.Refresh();
        fixture.Tracker.Start(new[] { Rule("wireproxy.exe", DirectPolicy), Rule("chrome.exe", DirectPolicy) });

        Assert.Equal(new uint[] { 200 }, fixture.Attached.Select(p => p.ProcessId));
    }

    // The tree poller this replaces only ever saw processes that started while it was watching, so
    // a browser opened before the engine kept its tabs out of the redirect.
    [Fact]
    public void Children_that_were_already_running_are_adopted_too()
    {
        using var fixture = new Fixture();
        fixture.Machine
            .Start(100, "chrome.exe")
            .Start(101, "chrome.exe", parentPid: 100)
            .Start(102, "crashpad.exe", parentPid: 101);
        fixture.Inventory.Refresh();

        fixture.Tracker.Start(new[] { Rule("chrome.exe", ProxyPolicy) });

        // 101 matches the filter on its own; 102 is there only because its grandparent does.
        Assert.Equal(new uint[] { 100, 101, 102 }, fixture.Attached.Select(p => p.ProcessId).OrderBy(id => id));
        Assert.Equal(new[] { ProxyPolicy }, fixture.Tracker.BuildPolicyMap()[102]);
    }

    // Windows never clears a parent id, so a long-lived process can name a pid that has since gone
    // to something unrelated — and adopting on that would redirect a stranger.
    [Fact]
    public void A_parent_that_started_after_its_supposed_child_is_not_a_parent()
    {
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

        using var fixture = new Fixture();
        fixture.Machine
            .Start(100, "chrome.exe", startedUtc: start.AddMinutes(5))
            .Start(101, "stranger.exe", parentPid: 100, startedUtc: start);
        fixture.Inventory.Refresh();

        fixture.Tracker.Start(new[] { Rule("chrome.exe", ProxyPolicy) });

        Assert.Equal(new uint[] { 100 }, fixture.Attached.Select(p => p.ProcessId));
    }

    [Fact]
    public void A_filter_about_arguments_is_answered_from_the_table()
    {
        using var fixture = new Fixture();
        fixture.Machine
            .Start(100, "java.exe", commandLine: "java -jar minecraft.jar")
            .Start(200, "java.exe", commandLine: "java -jar build-tool.jar");
        fixture.Inventory.Refresh();

        int readsBefore = fixture.Machine.TotalDetailReads;
        fixture.Tracker.Start(new[] { ArgumentRule("java.exe", "minecraft", ProxyPolicy) });

        Assert.Equal(new uint[] { 100 }, fixture.Attached.Select(p => p.ProcessId));

        // Not one query: the command lines were already in the table. This is what a filter edit
        // used to cost 210ms per redirected process to answer.
        Assert.Equal(readsBefore, fixture.Machine.TotalDetailReads);
    }

    [Fact]
    public void A_changed_policy_reaches_the_process_without_detaching_it()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "chrome.exe");
        fixture.Inventory.Refresh();
        fixture.Tracker.Start(new[] { Rule("chrome.exe", DirectPolicy) });
        fixture.Attached.Clear();

        fixture.Tracker.ApplyRules(new[] { Rule("chrome.exe", ProxyPolicy) });

        // Detaching and attaching again is what used to freeze the window on Save, and it dropped
        // every flow those processes had for an instant.
        Assert.Empty(fixture.Attached);
        Assert.Empty(fixture.Detached);
        Assert.Equal(new[] { ProxyPolicy }, fixture.Tracker.BuildPolicyMap()[100]);
    }

    [Fact]
    public void Children_follow_their_parents_new_policies()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "chrome.exe").Start(101, "crashpad.exe", parentPid: 100);
        fixture.Inventory.Refresh();
        fixture.Tracker.Start(new[] { Rule("chrome.exe", DirectPolicy) });

        fixture.Tracker.ApplyRules(new[] { Rule("chrome.exe", ProxyPolicy) });

        Assert.Equal(new[] { ProxyPolicy }, fixture.Tracker.BuildPolicyMap()[101]);
    }

    [Fact]
    public void A_process_no_filter_describes_any_more_is_detached_and_its_children_with_it()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "chrome.exe").Start(101, "crashpad.exe", parentPid: 100);
        fixture.Inventory.Refresh();
        fixture.Tracker.Start(new[] { Rule("chrome.exe", DirectPolicy) });

        fixture.Tracker.ApplyRules(new[] { Rule("firefox.exe", DirectPolicy) });

        Assert.Equal(new uint[] { 100, 101 }, fixture.Detached.Select(p => p.ProcessId).OrderBy(id => id));
        Assert.Empty(fixture.Tracker.Tracked);
    }

    [Fact]
    public void Saving_the_same_filters_again_changes_nothing()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "chrome.exe");
        fixture.Inventory.Refresh();

        ProcessRule[] rules = { Rule("chrome.exe", DirectPolicy) };
        fixture.Tracker.Start(rules);
        fixture.Attached.Clear();

        fixture.Tracker.ApplyRules(rules);

        Assert.Empty(fixture.Attached);
        Assert.Empty(fixture.Detached);
    }

    [Fact]
    public void A_process_named_directly_is_untouched_by_a_filter_edit()
    {
        using var fixture = new Fixture();
        fixture.Machine.Start(100, "anything.exe");
        fixture.Inventory.Refresh();
        fixture.Tracker.Start(Array.Empty<ProcessRule>());

        TrackedProcess? tracked = fixture.Tracker.AttachProcessId(100, ProxyPolicy);
        Assert.NotNull(tracked);

        fixture.Tracker.ApplyRules(new[] { Rule("something-else.exe", DirectPolicy) });

        // No filter claims it, so no filter edit can take it away.
        Assert.Empty(fixture.Detached);
        Assert.Equal(new[] { ProxyPolicy }, fixture.Tracker.BuildPolicyMap()[100]);
    }
}
