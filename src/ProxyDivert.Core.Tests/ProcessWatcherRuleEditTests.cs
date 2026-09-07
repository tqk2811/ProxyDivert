using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using TqkLibrary.WinDivert.ProcessControl.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Saving a filter used to detach and re-attach every process it described whenever its policy
// list changed — sixty browser processes, each closing and reopening a driver handle on the UI
// thread, and each losing its flows for an instant. The redirector only knows pids, so a routing
// change needs no detach at all. These pin that: the pid stays attached, its routing changes.
//
// Start() is never called: it puts up WMI hooks against the real machine.
public class ProcessWatcherRuleEditTests
{
    private const uint Browser = 910_000;
    private const uint Helper = 910_001;

    private static readonly Guid Work = Guid.NewGuid();
    private static readonly Guid Games = Guid.NewGuid();

    private static ProcessRule Rule(string exeName, params Guid[] policies) => new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = exeName,
        Condition = new ConditionGroup
        {
            Operator = ConditionOperator.All,
            Children = { new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = exeName } },
        },
        PolicyIds = policies.ToList(),
        IncludeChildren = true,
    };

    private sealed class Machine : IDisposable
    {
        public FakeProcessFinder Finder { get; } = new FakeProcessFinder();
        public ProcessWatcher Watcher { get; }
        public List<TrackedProcess> Attached { get; } = new List<TrackedProcess>();
        public List<TrackedProcess> Detached { get; } = new List<TrackedProcess>();

        public Machine(int browsers, bool withHelper = false)
        {
            for (uint i = 0; i < browsers; i++)
                Finder.Processes.Add(new ProcessInfo(Browser + i * 2, "chrome.exe", null));
            if (withHelper) Finder.Processes.Add(new ProcessInfo(Helper, "helper.exe", null));

            Watcher = new ProcessWatcher(NullLogger<ProcessWatcher>.Instance, Finder, new NoCommandLines());
            Watcher.ProcessAttached += Attached.Add;
            Watcher.ProcessDetached += Detached.Add;
        }

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void A_policy_change_re_routes_the_process_without_detaching_it()
    {
        using var machine = new Machine(browsers: 5);
        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Work) });
        Assert.Equal(5, machine.Attached.Count);

        ProcessRule edited = Rule("chrome.exe", Work, Games);
        machine.Watcher.ApplyRules(new[] { edited });

        Assert.Empty(machine.Detached);
        Assert.Equal(5, machine.Attached.Count);
        Assert.All(machine.Watcher.Tracked, p =>
        {
            Assert.Equal(new[] { Work, Games }, p.PolicyIds);
            Assert.Same(edited, p.MatchedRule);
        });
    }

    // The map the resolver routes by is built from the tracked set, so the swap has to be visible
    // there — otherwise the edit would be recorded and never used.
    [Fact]
    public void The_policy_map_reflects_the_edit()
    {
        using var machine = new Machine(browsers: 1);
        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Work) });

        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Games) });

        Assert.Equal(new[] { Games }, machine.Watcher.BuildPolicyMap()[Browser]);
    }

    [Fact]
    public void Children_follow_their_parents_new_policies()
    {
        using var machine = new Machine(browsers: 1, withHelper: true);
        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Work) });
        machine.Watcher.AttachChild(Helper, Browser);
        Assert.Equal(new[] { Work }, machine.Watcher.BuildPolicyMap()[Helper]);

        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Games) });

        Assert.Empty(machine.Detached);
        Assert.Equal(new[] { Games }, machine.Watcher.BuildPolicyMap()[Helper]);
    }

    [Fact]
    public void A_process_the_new_rules_no_longer_describe_is_detached_and_its_children_with_it()
    {
        using var machine = new Machine(browsers: 1, withHelper: true);
        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Work) });
        machine.Watcher.AttachChild(Helper, Browser);

        machine.Watcher.ApplyRules(new[] { Rule("firefox.exe", Work) });

        Assert.Empty(machine.Watcher.Tracked);
        Assert.Equal(new[] { Browser, Helper }.OrderBy(p => p), machine.Detached.Select(p => p.ProcessId).OrderBy(p => p));
    }

    // Saving without changing anything is the common case (the engine runs on a snapshot, so the
    // rule objects are new every time) and must stay silent.
    [Fact]
    public void Saving_the_same_rules_again_changes_nothing()
    {
        using var machine = new Machine(browsers: 3);
        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Work) });

        machine.Watcher.ApplyRules(new[] { Rule("chrome.exe", Work) });

        Assert.Empty(machine.Detached);
        Assert.Equal(3, machine.Attached.Count);
        Assert.All(machine.Watcher.Tracked, p => Assert.Equal(new[] { Work }, p.PolicyIds));
    }

    private sealed class FakeProcessFinder : IProcessFinder
    {
        public List<ProcessInfo> Processes { get; } = new List<ProcessInfo>();

        public IReadOnlyList<ProcessInfo> ListAll() => Processes;

        public IReadOnlyList<ProcessInfo> FindByName(string name)
            => Processes.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        public ProcessInfo? FindById(uint pid) => Processes.FirstOrDefault(p => p.Id == pid);
    }

    // No rule here asks about arguments, so nothing should ever be read; returning nothing keeps
    // the test honest if that changes.
    private sealed class NoCommandLines : IProcessCommandLineReader
    {
        public string? Read(uint processId) => null;

        public IReadOnlyDictionary<uint, string> ReadAll() => new Dictionary<uint, string>();
    }
}
