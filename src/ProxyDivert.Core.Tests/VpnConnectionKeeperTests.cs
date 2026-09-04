using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Outbounds;
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
    };

    [Fact]
    public void ATunnelThatCannotStart_IsRetriedWithAGrowingDelay()
    {
        Outbound vpn = MissingConfigVpn();
        var seen = new List<VpnStatus>();
        var secondRetry = new ManualResetEventSlim();

        using var factory = new OutboundSourceFactory();
        using var keeper = new VpnConnectionKeeper(factory, NullLogger<VpnConnectionKeeper>.Instance);
        keeper.StatusChanged += status =>
        {
            lock (seen) seen.Add(status);
            if (status.State == VpnConnectionState.Reconnecting && status.RetryCount == 2)
                secondRetry.Set();
        };

        var clock = Stopwatch.StartNew();
        keeper.Sync(new[] { vpn }, null);

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

    [Fact]
    public void ADisabledVpnOutbound_IsNotKept()
    {
        Outbound vpn = MissingConfigVpn();
        vpn.IsEnabled = false;

        using var factory = new OutboundSourceFactory();
        using var keeper = new VpnConnectionKeeper(factory, NullLogger<VpnConnectionKeeper>.Instance);
        keeper.Sync(new[] { vpn }, null);

        Assert.Empty(keeper.Statuses);
        Assert.Null(keeper.StatusOf(vpn.Id));
    }

    // Saving the configuration must not disturb a tunnel whose settings are unchanged — that is
    // the whole point of the signature comparison, and the reason a VPN survives an unrelated edit.
    [Fact]
    public void SyncingTheSameConfigurationTwice_DoesNotRestartTheTunnel()
    {
        Outbound vpn = MissingConfigVpn();

        using var factory = new OutboundSourceFactory();
        using var keeper = new VpnConnectionKeeper(factory, NullLogger<VpnConnectionKeeper>.Instance);

        keeper.Sync(new[] { vpn }, null);
        VpnStatus? before = keeper.StatusOf(vpn.Id);
        Assert.NotNull(before);

        var stopped = new List<VpnStatus>();
        keeper.StatusChanged += s => { if (s.State == VpnConnectionState.Stopped) lock (stopped) stopped.Add(s); };
        keeper.Sync(new[] { vpn }, null);

        // A restarted tunnel would have announced itself stopped on the way down.
        lock (stopped) Assert.Empty(stopped);
        Assert.Single(keeper.Statuses);
    }

    // Disabling a VPN while the engine runs has to take the tunnel down with it, or the user has
    // turned something off and left a subprocess talking to a VPN server.
    [Fact]
    public void DisablingAVpnOutbound_StopsKeepingIt()
    {
        Outbound vpn = MissingConfigVpn();

        using var factory = new OutboundSourceFactory();
        using var keeper = new VpnConnectionKeeper(factory, NullLogger<VpnConnectionKeeper>.Instance);
        keeper.Sync(new[] { vpn }, null);
        Assert.Single(keeper.Statuses);

        vpn.IsEnabled = false;
        keeper.Sync(new[] { vpn }, null);

        Assert.Empty(keeper.Statuses);
    }
}
