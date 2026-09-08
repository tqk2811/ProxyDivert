using System;
using System.IO;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What counts as "the user changed this outbound".
//
// Everything that has to decide whether what it is holding is still the right thing compares these
// strings — the registry for its instances, the VPN supervisor for its tunnels — so the rule lives
// in one place and these say what it is.
public class OutboundSignatureTests
{
    // The wireproxy binary is not part of an outbound, but every VPN instance is built from it, so
    // pointing the setting somewhere else has to count as a change to all of them.
    [Fact]
    public void OfAVpnOutbound_FollowsTheWireProxyPath()
    {
        var vpn = new Outbound { Id = Guid.NewGuid(), Name = "vpn", Kind = OutboundKind.Vpn, Url = "C:/none.conf" };

        Assert.Equal(OutboundSignature.Of(vpn, @"C:\a\wireproxy.exe"), OutboundSignature.Of(vpn, @"C:\a\wireproxy.exe"));
        Assert.NotEqual(OutboundSignature.Of(vpn, @"C:\a\wireproxy.exe"), OutboundSignature.Of(vpn, @"C:\b\wireproxy.exe"));
    }

    // A VPN's real settings are in the .conf the outbound points at, so editing that file must
    // reconnect the tunnel even though nothing in the outbound itself changed.
    [Fact]
    public void OfAVpnOutbound_FollowsTheConfigFileContent()
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
    public void IgnoresTheName()
    {
        Guid id = Guid.NewGuid();
        Outbound before = Socks5(id);
        Outbound after = Socks5(id);
        after.Name = "something else entirely";

        Assert.Equal(OutboundSignature.Of(before), OutboundSignature.Of(after));
    }

    private static Outbound Socks5(Guid id) => new Outbound
    {
        Id = id,
        Name = "proxy",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
    };
}
