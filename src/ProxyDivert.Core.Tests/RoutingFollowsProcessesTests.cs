using System;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The routing table is built once per configuration and then read for as long as that
// configuration stands. Which process is under redirection, and under which policy, keeps changing
// underneath it — a browser start attaches sixty processes in a few seconds — and these say the
// table follows that without being rebuilt.
public class RoutingFollowsProcessesTests : IDisposable
{
    private static readonly Guid DirectPolicyId = Guid.NewGuid();
    private static readonly Guid ProxyPolicyId = Guid.NewGuid();
    private const uint Pid = 100;

    private readonly FakeProcessMachine _machine = new FakeProcessMachine();
    private readonly ProcessInventory _inventory;
    private readonly ProcessRuleTracker _tracker;

    private static readonly Outbound Socks5 = new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "socks5",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
    };

    public RoutingFollowsProcessesTests()
    {
        // Something the filters never match, so the listing is never empty: an empty listing is how
        // a failed enumeration looks, and the table rightly refuses to retire the whole machine over
        // one.
        _machine.Start(4, "System");
        _inventory = new ProcessInventory(NullLogger<ProcessInventory>.Instance, _machine, _machine);
        _tracker = new ProcessRuleTracker(NullLogger<ProcessRuleTracker>.Instance, _inventory);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _inventory.Dispose();
    }

    private static RoutingPolicy Policy(Guid id, Guid outboundId)
    {
        var policy = new RoutingPolicy { Id = id, Name = id.ToString(), OutboundId = outboundId };
        policy.Rules.Add(new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = HostMatcherType.Wildcard,
            Pattern = "*",
        });
        return policy;
    }

    private static ProcessRule Filter(string exe, Guid policyId)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = exe,
            IncludeChildren = true,
            Condition = new ConditionGroup
            {
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = exe },
                },
            },
            PolicyIds = new System.Collections.Generic.List<Guid> { policyId },
        };

    // Built the way the engine builds it: from the configuration alone, with the tracker as the
    // live answer to "which policies has this pid".
    private RoutingPolicyResolver BuildTable()
        => new RoutingPolicyResolver(
            CompiledRuleSet.Compile(new[]
            {
                Policy(ProxyPolicyId, Socks5.Id),
                Policy(DirectPolicyId, Outbound.DirectId),
            }),
            new[] { Socks5 },
            _tracker);

    private static RouteTarget Target()
        => new RouteTarget(Pid, IPAddress.Parse("93.184.216.34"), 443, "example.com");

    private void Settle()
    {
        _inventory.Refresh();
        _tracker.MatchEverything();
    }

    [Fact]
    public void AProcessAttachedAfterTheTableWasBuilt_IsRoutedByItAnyway()
    {
        _tracker.Start(new[] { Filter("chrome.exe", ProxyPolicyId) }, attachFromProcessEvents: false);
        RoutingPolicyResolver table = BuildTable();
        Assert.True(table.Resolve(Target()).IsDirect);

        _machine.Start(Pid, "chrome.exe");
        Settle();

        Assert.Equal(Socks5.Id, table.Resolve(Target()).Outbound.Id);
    }

    [Fact]
    public void AFilterEditMovingAProcessToAnotherPolicy_ReachesTheSameTable()
    {
        // The re-description happens in place, without a detach and re-attach, so nothing here
        // raises an event the table could have listened to. It reads the tracker instead.
        _tracker.Start(new[] { Filter("chrome.exe", ProxyPolicyId) }, attachFromProcessEvents: false);
        _machine.Start(Pid, "chrome.exe");
        Settle();

        RoutingPolicyResolver table = BuildTable();
        Assert.Equal(Socks5.Id, table.Resolve(Target()).Outbound.Id);

        _tracker.ApplyRules(new[] { Filter("chrome.exe", DirectPolicyId) });

        Assert.True(table.Resolve(Target()).IsDirect);
    }

    [Fact]
    public void AProcessThatExited_StopsBeingClaimedWithoutRebuildingAnything()
    {
        _tracker.Start(new[] { Filter("chrome.exe", ProxyPolicyId) }, attachFromProcessEvents: false);
        _machine.Start(Pid, "chrome.exe");
        Settle();

        RoutingPolicyResolver table = BuildTable();
        Assert.Equal(Socks5.Id, table.Resolve(Target()).Outbound.Id);

        _machine.Exit(Pid);
        _inventory.Refresh();

        // A pid nothing claims is not blocked and not tunnelled: it is a process the user never
        // asked us to touch, and the relay can still see a connection from one.
        Assert.True(table.Resolve(Target()).IsDirect);
    }

    [Fact]
    public void AChildInheritingItsParentsPolicies_IsRoutedByThemToo()
    {
        _tracker.Start(new[] { Filter("launcher.exe", ProxyPolicyId) }, attachFromProcessEvents: false);
        _machine.Start(Pid, "launcher.exe");
        Settle();

        RoutingPolicyResolver table = BuildTable();

        _machine.Start(200, "game.exe", parentPid: Pid);
        Settle();

        var child = new RouteTarget(200, IPAddress.Parse("93.184.216.34"), 443, "example.com");
        Assert.Equal(Socks5.Id, table.Resolve(child).Outbound.Id);
    }
}
