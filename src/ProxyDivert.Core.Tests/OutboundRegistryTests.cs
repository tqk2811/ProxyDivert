using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What a saved configuration is allowed to disturb, and who is allowed to disturb it.
//
// The registry used to throw every live instance away whenever the user pressed Save, which for a
// VPN meant killing wireproxy and re-handshaking the tunnel because a checkbox on another tab had
// changed. These tests pin down the opposite: an outbound nobody edited keeps the exact instance it
// had, and one that was edited does not.
//
// They also pin down the other half, which is ownership. Whatever drops an instance — an edit, or
// the supervisor giving up on a tunnel — it is dropped here, disposed once, and announced, because
// the UDP tunnels and the learned IPv6 answers keyed by that outbound are stale the moment it goes.
public class OutboundRegistryTests
{
    private static Outbound Socks5(Guid id, string url = "socks5://127.0.0.1:1080") => new Outbound
    {
        Id = id,
        Name = "proxy",
        Kind = OutboundKind.Socks5,
        Url = url,
    };

    private static OutboundRegistry Registry() => new OutboundRegistry(OutboundSourceFactory.CreateDefault());

    private static OutboundRegistry Registry(params IOutboundSourceBuilder[] builders)
        => new OutboundRegistry(new OutboundSourceFactory(builders));

    [Fact]
    public async Task Reconcile_KeepsTheInstanceOfAnUntouchedOutbound()
    {
        Guid id = Guid.NewGuid();
        Outbound outbound = Socks5(id);
        using var registry = Registry();

        IProxySource first = registry.GetOrCreate(outbound).Source;
        IReadOnlyCollection<Guid> dropped = await registry.ReconcileAsync(new[] { outbound }, null);

        Assert.Empty(dropped);
        Assert.Same(first, registry.GetOrCreate(outbound).Source);
    }

    [Fact]
    public async Task Reconcile_RebuildsAnEditedOutbound()
    {
        Guid id = Guid.NewGuid();
        using var registry = Registry();
        IProxySource first = registry.GetOrCreate(Socks5(id)).Source;

        Outbound edited = Socks5(id, "socks5://127.0.0.1:9999");
        IReadOnlyCollection<Guid> dropped = await registry.ReconcileAsync(new[] { edited }, null);

        Assert.Equal(new[] { id }, dropped);
        Assert.NotSame(first, registry.GetOrCreate(edited).Source);
    }

    [Fact]
    public async Task Reconcile_DropsAnOutboundThatIsGone()
    {
        Guid id = Guid.NewGuid();
        using var registry = Registry();
        registry.GetOrCreate(Socks5(id));

        IReadOnlyCollection<Guid> dropped = await registry.ReconcileAsync(Array.Empty<Outbound>(), null);

        Assert.Equal(new[] { id }, dropped);
        Assert.Null(registry.Find(id));
    }

    [Fact]
    public async Task Reconcile_LeavesTheOtherOutboundsAloneWhenOneChanges()
    {
        Guid edited = Guid.NewGuid();
        Guid untouched = Guid.NewGuid();
        using var registry = Registry();
        registry.GetOrCreate(Socks5(edited));
        IProxySource keep = registry.GetOrCreate(Socks5(untouched)).Source;

        IReadOnlyCollection<Guid> dropped = await registry.ReconcileAsync(
            new[] { Socks5(edited, "socks5://127.0.0.1:2222"), Socks5(untouched) }, null);

        Assert.Equal(new[] { edited }, dropped);
        Assert.Same(keep, registry.Find(untouched)?.Source);
    }

    // An instance is released exactly once, by its owner, and everything keyed by that outbound is
    // told. Before there was an owner, the instance was disposed by whichever of three objects
    // noticed first, and the ones that had not noticed carried on using it.
    [Fact]
    public async Task Reconcile_DisposesTheInstanceItDropsAndSaysSo()
    {
        var builder = new FakeOutboundSourceBuilder(OutboundKind.Socks5);
        Guid id = Guid.NewGuid();
        using var registry = Registry(builder);

        var announced = new List<Guid>();
        registry.InstanceDropped += dropped => { lock (announced) announced.Add(dropped); };

        registry.GetOrCreate(Socks5(id));
        await registry.ReconcileAsync(Array.Empty<Outbound>(), null);

        Assert.Equal(1, builder.Builds[0].Source.Disposals);
        lock (announced) Assert.Equal(new[] { id }, announced);
    }

    // The supervisor's way of saying "the tunnel I watch is finished". It reads differently from a
    // configuration change on purpose, but it must have the same effect: the next caller gets a
    // freshly built instance, so a configuration the user has since corrected is picked up.
    [Fact]
    public async Task Discard_ThrowsTheInstanceAwayAndBuildsAFreshOneNextTime()
    {
        var builder = new FakeOutboundSourceBuilder(OutboundKind.Socks5);
        Outbound outbound = Socks5(Guid.NewGuid());
        using var registry = Registry(builder);

        IProxySource first = registry.GetOrCreate(outbound).Source;
        await registry.DiscardAsync(outbound.Id, "the tunnel is down");

        Assert.Equal(1, builder.Builds[0].Source.Disposals);
        Assert.NotSame(first, registry.GetOrCreate(outbound).Source);
        Assert.Equal(2, builder.Builds.Count);
    }

    // Wiring, not behaviour: whichever builder the container happened to enumerate first would win
    // silently, and the outbound would be built by something other than what its author meant.
    [Fact]
    public void TwoBuildersClaimingOneKind_AreRefusedWhenTheyAreWiredUp()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new OutboundSourceFactory(new IOutboundSourceBuilder[]
            {
                new FakeOutboundSourceBuilder(OutboundKind.Socks5),
                new Socks5OutboundBuilder(),
            }));

        Assert.Contains("Socks5", error.Message, StringComparison.Ordinal);
    }
}
