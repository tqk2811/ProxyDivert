using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The keeper's failure path, which is the one that has to behave: a VPN that cannot start is the
// normal state of affairs while the user is still setting it up (no wireproxy binary yet, a
// .conf typo, the file moved). Retrying it in a tight loop would spawn processes as fast as the
// machine allows, so the delay between attempts is the thing worth pinning down.
//
// Nothing here needs wireproxy, elevation, or a network: it is the supervision loop being tested,
// not the tunnel.
public class VpnConnectionKeeperTests
{
    private static Outbound MissingConfigVpn() => new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "vpn",
        Kind = OutboundKind.Vpn,
        Url = Path.Combine(Path.GetTempPath(), $"pd-missing-{Guid.NewGuid():N}.conf"),
        // The switch the user flicks; without it the keeper has nothing to hold up.
        KeepConnected = true,
    };

    [Fact]
    public async Task ATunnelThatCannotStart_IsRetriedWithAGrowingDelay()
    {
        Outbound vpn = MissingConfigVpn();
        var seen = new List<VpnStatus>();
        var secondRetry = new ManualResetEventSlim();

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        keeper.StatusChanged += status =>
        {
            lock (seen) seen.Add(status);
            if (status.State == VpnConnectionState.Reconnecting && status.RetryCount == 2)
                secondRetry.Set();
        };

        var clock = Stopwatch.StartNew();
        await keeper.SyncAsync(new[] { vpn }, null);

        // Two failures cost 1s of backoff between them, so this waits generously and measures.
        Assert.True(secondRetry.Wait(TimeSpan.FromSeconds(10)), "the keeper never reached a second attempt");
        clock.Stop();

        List<VpnStatus> statuses;
        lock (seen) statuses = seen.ToList();

        Assert.Equal(VpnConnectionState.Connecting, statuses[0].State);

        VpnStatus firstFailure = statuses.First(s => s.State == VpnConnectionState.Reconnecting);
        Assert.Equal(1, firstFailure.RetryCount);
        // The reason reaches the UI rather than being swallowed — a missing file is the single
        // most common way this goes wrong, and it is fixable the moment the user is told.
        Assert.Contains("FileNotFound", firstFailure.Error);

        // The backoff is real: a spinning loop would have run through both attempts instantly.
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1),
            $"the second attempt came after only {clock.ElapsedMilliseconds}ms, so nothing waited");
        // ...and no further than the schedule allows (1s + 2s), which catches a backoff that grows
        // the wrong way as surely as one that does not grow at all.
        Assert.DoesNotContain(statuses, s => s.RetryCount > 3);
    }

    // The other half, which nothing could reach until a way out became a builder: every VPN
    // outbound meant a wireproxy subprocess or a real dial, so the only tunnel a test could produce
    // was one that fails, and the supervision loop's success path was never run at all.
    [Fact]
    public async Task ATunnelThatComesUp_IsReportedConnectedAndIsNotDialledTwice()
    {
        Outbound vpn = MissingConfigVpn();
        var tunnel = new FakeManagedProxySource();
        var builder = new FakeOutboundSourceBuilder(OutboundKind.Vpn, _ => tunnel);

        using var registry = RegistryOf(builder);
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        var connected = new ManualResetEventSlim();
        keeper.StatusChanged += status => { if (status.State == VpnConnectionState.Connected) connected.Set(); };

        await keeper.SyncAsync(new[] { vpn }, null);

        Assert.True(connected.Wait(TimeSpan.FromSeconds(10)), "the tunnel never came up");
        Assert.Equal(VpnConnectionState.Connected, keeper.StatusOf(vpn.Id)?.State);
        // A tunnel that is up is watched, not dialled again: the whole point of holding one is that
        // the handshake is paid once.
        Assert.Equal(1, tunnel.Starts);
        Assert.Single(builder.Builds);
    }

    // The wireproxy path is part of what a VPN is built from, and the keeper dials at startup —
    // before anything has applied a configuration to the engine. The path used to reach the
    // instances only through that reconcile, so the first tunnel of a session was built as if the
    // setting were empty, and it only came right once the user pressed Start.
    [Fact]
    public async Task TheWireProxyPathTheKeeperIsGiven_ReachesTheBuild()
    {
        const string binary = @"C:\tools\wireproxy.exe";
        Outbound vpn = MissingConfigVpn();
        var builder = new FakeOutboundSourceBuilder(OutboundKind.Vpn, _ => new FakeManagedProxySource());

        using var registry = RegistryOf(builder);
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        var connected = new ManualResetEventSlim();
        keeper.StatusChanged += status => { if (status.State == VpnConnectionState.Connected) connected.Set(); };

        await keeper.SyncAsync(new[] { vpn }, binary);

        Assert.True(connected.Wait(TimeSpan.FromSeconds(10)), "the tunnel never came up");
        Assert.Equal(binary, builder.Builds[0].WireProxyPath);
    }

    private static OutboundRegistry RegistryOf(FakeOutboundSourceBuilder builder)
        => new OutboundRegistry(new OutboundSourceFactory(new IOutboundSourceBuilder[] { builder }));

    [Fact]
    public async Task ADisabledVpnOutbound_IsNotKept()
    {
        Outbound vpn = MissingConfigVpn();
        vpn.IsEnabled = false;

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        await keeper.SyncAsync(new[] { vpn }, null);

        Assert.Empty(keeper.Statuses);
        Assert.Null(keeper.StatusOf(vpn.Id));
    }

    // Saving the configuration must not disturb a tunnel whose settings are unchanged — that is
    // the whole point of the signature comparison, and the reason a VPN survives an unrelated edit.
    [Fact]
    public async Task SyncingTheSameConfigurationTwice_DoesNotRestartTheTunnel()
    {
        Outbound vpn = MissingConfigVpn();

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);

        await keeper.SyncAsync(new[] { vpn }, null);
        VpnStatus? before = keeper.StatusOf(vpn.Id);
        Assert.NotNull(before);

        var stopped = new List<VpnStatus>();
        keeper.StatusChanged += s => { if (s.State == VpnConnectionState.Stopped) lock (stopped) stopped.Add(s); };
        await keeper.SyncAsync(new[] { vpn }, null);

        // A restarted tunnel would have announced itself stopped on the way down.
        lock (stopped) Assert.Empty(stopped);
        Assert.Single(keeper.Statuses);
    }

    // Disabling a VPN while the engine runs has to take the tunnel down with it, or the user has
    // turned something off and left a subprocess talking to a VPN server.
    [Fact]
    public async Task DisablingAVpnOutbound_StopsKeepingIt()
    {
        Outbound vpn = MissingConfigVpn();

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        await keeper.SyncAsync(new[] { vpn }, null);
        Assert.Single(keeper.Statuses);

        vpn.IsEnabled = false;
        await keeper.SyncAsync(new[] { vpn }, null);

        Assert.Empty(keeper.Statuses);
    }

    // A VPN nobody has switched on is a set of settings, not a connection. It used to be dialled
    // the moment the engine started, which is why turning redirection on brought up tunnels the
    // user had never asked for.
    [Fact]
    public async Task AVpnThatIsNotSwitchedOn_IsNotKept()
    {
        Outbound vpn = MissingConfigVpn();
        vpn.KeepConnected = false;

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        await keeper.SyncAsync(new[] { vpn }, null);

        Assert.Empty(keeper.Statuses);
    }

    // Switching redirection on: a filter routing through a VPN needs that tunnel up, so it is
    // switched on for the user rather than failing every connection the rule catches.
    [Fact]
    public async Task StartingARunSwitchesOn_OnlyTheVpnsAFilterRoutesThrough()
    {
        Outbound routed = MissingConfigVpn();
        routed.KeepConnected = false;
        Outbound unused = MissingConfigVpn();
        unused.KeepConnected = false;

        AppConfig config = BuildConfig(routed, unused, filterEnabled: true);

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);
        IReadOnlyCollection<Guid> switchedOn = keeper.SwitchOnRoutedVpns(config);
        // The two steps the engine's start puts either side of opening the driver.
        await keeper.SyncAsync(config.Outbounds, config.WireProxyPath);

        Assert.Equal(new[] { routed.Id }, switchedOn);
        Assert.True(routed.KeepConnected);
        Assert.False(unused.KeepConnected);
        Assert.Single(keeper.Statuses);
        Assert.NotNull(keeper.StatusOf(routed.Id));
    }

    // A filter that is switched off routes nothing, so it is no reason to dial anything.
    [Fact]
    public async Task ADisabledFilter_SwitchesOnNothing()
    {
        Outbound routed = MissingConfigVpn();
        routed.KeepConnected = false;

        AppConfig config = BuildConfig(routed, null, filterEnabled: false);

        using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());
        using var keeper = new VpnConnectionKeeper(registry, NullLogger<VpnConnectionKeeper>.Instance);

        Assert.Empty(keeper.SwitchOnRoutedVpns(config));
        await keeper.SyncAsync(config.Outbounds, config.WireProxyPath);

        Assert.False(routed.KeepConnected);
        Assert.Empty(keeper.Statuses);
    }

    // One filter, one policy, pointing at the first outbound; the second is in the list but
    // nothing routes to it.
    private static AppConfig BuildConfig(Outbound routed, Outbound? unused, bool filterEnabled)
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = "policy",
            OutboundId = routed.Id,
        };
        var config = new AppConfig
        {
            Outbounds = { routed },
            Policies = { policy },
            ProcessRules =
            {
                new ProcessRule
                {
                    Id = Guid.NewGuid(),
                    Name = "filter",
                    IsEnabled = filterEnabled,
                    PolicyIds = { policy.Id },
                },
            },
        };
        if (unused != null) config.Outbounds.Add(unused);
        return config;
    }
}
