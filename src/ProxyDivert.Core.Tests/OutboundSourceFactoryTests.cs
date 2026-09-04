using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What a saved configuration is allowed to disturb.
//
// The factory used to throw every live instance away whenever the user pressed Save, which for a
// VPN meant killing wireproxy and re-handshaking the tunnel because a checkbox on another tab had
// changed. These tests pin down the opposite: an outbound nobody edited keeps the exact instance
// it had, and one that was edited does not.
public class OutboundSourceFactoryTests
{
    private static Outbound Socks5(Guid id, string url = "socks5://127.0.0.1:1080") => new Outbound
    {
        Id = id,
        Name = "proxy",
        Kind = OutboundKind.Socks5,
        Url = url,
    };

    [Fact]
    public void ApplyOutbounds_KeepsTheInstanceOfAnUntouchedOutbound()
    {
        Guid id = Guid.NewGuid();
        Outbound outbound = Socks5(id);
        using var factory = new OutboundSourceFactory();

        IProxySource first = factory.GetOrCreate(outbound);
        IReadOnlyCollection<Guid> invalidated = factory.ApplyOutbounds(new[] { outbound }, null);

        Assert.Empty(invalidated);
        Assert.Same(first, factory.GetOrCreate(outbound));
    }

    [Fact]
    public void ApplyOutbounds_RebuildsAnEditedOutbound()
    {
        Guid id = Guid.NewGuid();
        using var factory = new OutboundSourceFactory();
        IProxySource first = factory.GetOrCreate(Socks5(id));

        Outbound edited = Socks5(id, "socks5://127.0.0.1:9999");
        IReadOnlyCollection<Guid> invalidated = factory.ApplyOutbounds(new[] { edited }, null);

        Assert.Equal(new[] { id }, invalidated);
        Assert.NotSame(first, factory.GetOrCreate(edited));
    }

    [Fact]
    public void ApplyOutbounds_DropsAnOutboundThatIsGone()
    {
        Guid id = Guid.NewGuid();
        using var factory = new OutboundSourceFactory();
        factory.GetOrCreate(Socks5(id));

        IReadOnlyCollection<Guid> invalidated = factory.ApplyOutbounds(Array.Empty<Outbound>(), null);

        Assert.Equal(new[] { id }, invalidated);
        Assert.Null(factory.Find(id));
    }

    [Fact]
    public void ApplyOutbounds_LeavesTheOtherOutboundsAloneWhenOneChanges()
    {
        Guid edited = Guid.NewGuid();
        Guid untouched = Guid.NewGuid();
        using var factory = new OutboundSourceFactory();
        factory.GetOrCreate(Socks5(edited));
        IProxySource keep = factory.GetOrCreate(Socks5(untouched));

        IReadOnlyCollection<Guid> invalidated = factory.ApplyOutbounds(
            new[] { Socks5(edited, "socks5://127.0.0.1:2222"), Socks5(untouched) }, null);

        Assert.Equal(new[] { edited }, invalidated);
        Assert.Same(keep, factory.Find(untouched));
    }

    // The wireproxy binary is not part of an outbound, but every VPN instance is built from it, so
    // pointing the setting somewhere else has to count as a change to all of them.
    [Fact]
    public void Signature_OfAVpnOutbound_FollowsTheWireProxyPath()
    {
        var vpn = new Outbound { Id = Guid.NewGuid(), Name = "vpn", Kind = OutboundKind.Vpn, Url = "C:/none.conf" };

        Assert.Equal(OutboundSignature.Of(vpn, @"C:\a\wireproxy.exe"), OutboundSignature.Of(vpn, @"C:\a\wireproxy.exe"));
        Assert.NotEqual(OutboundSignature.Of(vpn, @"C:\a\wireproxy.exe"), OutboundSignature.Of(vpn, @"C:\b\wireproxy.exe"));
    }

    // A VPN's real settings are in the .conf the outbound points at, so editing that file must
    // reconnect the tunnel even though nothing in the outbound itself changed.
    [Fact]
    public void Signature_OfAVpnOutbound_FollowsTheConfigFileContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pd-sig-{Guid.NewGuid():N}.conf");
        try
        {
            File.WriteAllText(path, "[Interface]\n");
            var vpn = new Outbound { Id = Guid.NewGuid(), Name = "vpn", Kind = OutboundKind.Vpn, Url = path };
            string before = OutboundSignature.Of(vpn, null);

            File.WriteAllText(path, "[Interface]\nAddress = 10.0.0.2/32\n");

            Assert.NotEqual(before, OutboundSignature.Of(vpn, null));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // The name is a label; renaming an outbound is not a reason to drop a tunnel that works.
    [Fact]
    public void Signature_IgnoresTheName()
    {
        Guid id = Guid.NewGuid();
        Outbound before = Socks5(id);
        Outbound after = Socks5(id);
        after.Name = "something else entirely";

        Assert.Equal(OutboundSignature.Of(before), OutboundSignature.Of(after));
    }
}
