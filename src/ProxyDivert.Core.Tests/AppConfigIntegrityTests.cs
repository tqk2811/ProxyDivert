using System;
using System.Linq;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The configuration is three lists that reference each other by id, and nothing in the type system
// keeps them in step. These are the rules that do — the ones that used to be spread over two view
// models, the config store and the resolver, each with its own idea of what a repair was.
public class AppConfigIntegrityTests
{
    private static Outbound Proxy(Guid id, string name = "work") => new Outbound
    {
        Id = id,
        Name = name,
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
    };

    private static RoutingPolicy Policy(string name, Guid? id = null) => new RoutingPolicy
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
    };

    // ==== removing a policy ====

    [Fact]
    public void APolicyThatIsDeleted_LeavesNoFilterStillNamingIt()
    {
        AppConfig config = AppConfig.CreateDefault();
        RoutingPolicy doomed = Policy("streaming");
        config.Policies.Add(doomed);

        var filter = new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "chrome",
            PolicyIds = { config.Policies[0].Id, doomed.Id },
            PolicyOrder = { doomed.Id, config.Policies[0].Id },
        };
        config.ProcessRules.Add(filter);

        Assert.True(config.RemovePolicy(doomed.Id));

        Assert.DoesNotContain(doomed.Id, filter.PolicyIds);
        // Out of the arrangement as well: it remembers where a row sat in the editor, and a policy
        // that no longer exists has no place to remember.
        Assert.DoesNotContain(doomed.Id, filter.PolicyOrder);
        Assert.Equal(new[] { config.Policies[0].Id }, filter.PolicyIds);
    }

    // The dangerous direction. A filter still catches its processes after losing its last policy —
    // it just has no rules for them, so every connection resolves to nothing and leaves direct,
    // under the user's own address, with the window showing a filter that looks configured.
    [Fact]
    public void AFilterLeftWithNoPolicy_IsGivenOneRatherThanQuietlyGoingDirect()
    {
        AppConfig config = AppConfig.CreateDefault();
        RoutingPolicy doomed = Policy("only one it used");
        config.Policies.Add(doomed);

        var filter = new ProcessRule { Id = Guid.NewGuid(), Name = "game", PolicyIds = { doomed.Id } };
        config.ProcessRules.Add(filter);

        config.RemovePolicy(doomed.Id);

        Guid replacement = Assert.Single(filter.PolicyIds);
        Assert.Equal(config.Policies[0].Id, replacement);
        Assert.Contains(replacement, filter.PolicyOrder);
    }

    [Fact]
    public void TheLastPolicy_IsRefusedRatherThanRemoved()
    {
        AppConfig config = AppConfig.CreateDefault();
        Guid onlyOne = Assert.Single(config.Policies).Id;

        Assert.False(config.RemovePolicy(onlyOne));
        Assert.Single(config.Policies);
    }

    [Fact]
    public void APolicyThatIsNotThere_IsRefusedInsteadOfThrowing()
    {
        AppConfig config = AppConfig.CreateDefault();
        config.Policies.Add(Policy("second"));

        Assert.False(config.RemovePolicy(Guid.NewGuid()));
        Assert.Equal(2, config.Policies.Count);
    }

    // ==== removing an outbound ====

    // Block, never Direct. A policy whose way out has gone is a mistake, and a mistake that shows
    // up as a failed connection is one the user can find; falling back to Direct would put their
    // own address on the wire and say nothing about it.
    [Fact]
    public void APolicyWhoseOutboundIsDeleted_GoesToBlockAndNotToDirect()
    {
        AppConfig config = AppConfig.CreateDefault();
        Outbound proxy = Proxy(Guid.NewGuid());
        config.Outbounds.Add(proxy);

        RoutingPolicy policy = config.Policies[0];
        policy.OutboundId = proxy.Id;

        Assert.True(config.RemoveOutbound(proxy.Id));
        Assert.Equal(Outbound.BlockId, policy.OutboundId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABuiltInOutbound_IsNotTheUsersToDelete(bool direct)
    {
        AppConfig config = AppConfig.CreateDefault();
        Guid id = direct ? Outbound.DirectId : Outbound.BlockId;

        Assert.False(config.RemoveOutbound(id));
        Assert.Contains(config.Outbounds, o => o.Id == id);
    }

    // ==== normalising a file somebody edited ====

    [Fact]
    public void AFileThatLostItsBuiltIns_GetsThemBackSoTheWindowCanOfferThem()
    {
        var config = new AppConfig { Policies = { Policy("p") } };

        config.Normalize();

        Assert.Contains(config.Outbounds, o => o.Id == Outbound.DirectId && o.Kind == OutboundKind.Direct);
        Assert.Contains(config.Outbounds, o => o.Id == Outbound.BlockId && o.Kind == OutboundKind.Block);
    }

    [Fact]
    public void ABuiltInEditedIntoSomethingElse_ComesBackAsItselfButKeepsItsName()
    {
        var config = new AppConfig
        {
            Outbounds =
            {
                new Outbound
                {
                    Id = Outbound.DirectId,
                    Name = "my own name for it",
                    Kind = OutboundKind.HttpProxy,
                    Url = "http://127.0.0.1:8080",
                    Password = "p",
                    VpnProtocol = VpnProtocol.SoftEther,
                    IsEnabled = false,
                },
            },
            Policies = { Policy("p") },
        };

        config.Normalize();

        Outbound direct = config.Outbounds.Single(o => o.Id == Outbound.DirectId);
        Assert.Equal(OutboundKind.Direct, direct.Kind);
        Assert.Null(direct.Url);
        Assert.Null(direct.Password);
        Assert.Equal(VpnProtocol.Auto, direct.VpnProtocol);
        Assert.True(direct.IsEnabled);
        // Policies point at it by id, so what it is called is the user's business.
        Assert.Equal("my own name for it", direct.Name);
    }

    [Fact]
    public void APolicyPointingAtAnOutboundNobodyHas_ReadsAsBlock()
    {
        AppConfig config = AppConfig.CreateDefault();
        config.Policies[0].OutboundId = Guid.NewGuid();

        config.Normalize();

        Assert.Equal(Outbound.BlockId, config.Policies[0].OutboundId);
    }

    [Fact]
    public void AFilterNamingAPolicyDeletedByHand_IsRepairedOnTheWayIn()
    {
        AppConfig config = AppConfig.CreateDefault();
        Guid gone = Guid.NewGuid();
        var filter = new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "chrome",
            PolicyIds = { gone },
            PolicyOrder = { gone },
        };
        config.ProcessRules.Add(filter);

        config.Normalize();

        Assert.DoesNotContain(gone, filter.PolicyIds);
        Assert.DoesNotContain(gone, filter.PolicyOrder);
        Assert.Equal(config.Policies[0].Id, Assert.Single(filter.PolicyIds));
    }

    [Fact]
    public void AConfigWithNoPolicyAtAll_GetsOneToPointAt()
    {
        var config = new AppConfig();

        config.Normalize();

        RoutingPolicy policy = Assert.Single(config.Policies);
        Assert.Equal(Outbound.DirectId, policy.OutboundId);
        Assert.Empty(policy.Rules);
    }

    // Two rows sharing an id is not a question routing can answer — asked for that policy it would
    // have to pick one. Every table built from these lists is filled by assignment, so the last one
    // written is the one that survives, and the lists are made to agree with that.
    [Fact]
    public void TwoPoliciesSharingAnId_LeaveTheLastOneWritten()
    {
        Guid shared = Guid.NewGuid();
        var config = new AppConfig
        {
            Policies = { Policy("first", shared), Policy("second", shared) },
        };

        config.Normalize();

        RoutingPolicy survivor = Assert.Single(config.Policies);
        Assert.Equal("second", survivor.Name);
    }

    [Fact]
    public void TwoOutboundsSharingAnId_LeaveTheLastOneWritten()
    {
        Guid shared = Guid.NewGuid();
        var config = new AppConfig
        {
            Outbounds = { Proxy(shared, "old"), Proxy(shared, "new") },
            Policies = { Policy("p") },
        };

        config.Normalize();

        Outbound survivor = Assert.Single(config.Outbounds, o => o.Id == shared);
        Assert.Equal("new", survivor.Name);
    }

    // Two outbounds with the same id used to take the engine down on the way up: the resolver built
    // its table with ToDictionary, which throws, and the throw came out of StartAsync — so a
    // duplicated line in a hand-edited file left the machine with no redirection at all.
    [Fact]
    public void TwoOutboundsSharingAnId_DoNotStopTheResolverFromBeingBuilt()
    {
        Guid shared = Guid.NewGuid();
        var policy = new RoutingPolicy { Id = Guid.NewGuid(), Name = "p", OutboundId = shared };

        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Proxy(shared, "old"), Proxy(shared, "new") },
            policiesByProcessId: null);

        Assert.NotNull(resolver);
    }

    [Fact]
    public void NormalizingTwice_ChangesNothingTheSecondTime()
    {
        AppConfig config = AppConfig.CreateDefault();
        config.Policies[0].OutboundId = Guid.NewGuid();
        config.ProcessRules.Add(new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "chrome",
            PolicyIds = { Guid.NewGuid() },
        });

        string once = Describe(config.Normalize());
        string twice = Describe(config.Normalize());

        Assert.Equal(once, twice);
    }

    // The whole configuration as one comparable string. Idempotence is the property worth pinning:
    // this runs on every load and on every save, so a repair that keeps changing its mind would
    // rewrite the file each time the window opened.
    private static string Describe(AppConfig config)
        => System.Text.Json.JsonSerializer.Serialize(config);

    // ==== which copy gets repaired ====

    // The engine should never have to reason about a reference that goes nowhere, so its snapshot
    // is normalised. The instance the window is editing is not: repairing it under the user
    // mid-edit would move rows they are looking at.
    [Fact]
    public void TheEnginesSnapshotIsRepaired_TheWindowsOwnCopyIsLeftAlone()
    {
        AppConfig config = AppConfig.CreateDefault();
        Guid gone = Guid.NewGuid();
        config.ProcessRules.Add(new ProcessRule { Id = Guid.NewGuid(), Name = "chrome", PolicyIds = { gone } });

        AppConfig snapshot = ConfigStore.Clone(config);

        Assert.DoesNotContain(gone, snapshot.ProcessRules[0].PolicyIds);
        Assert.Contains(gone, config.ProcessRules[0].PolicyIds);
    }
}
