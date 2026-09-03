using System;
using System.Net;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Enums;
using TqkLibrary.Proxy.Helpers;
using TqkLibrary.WinDivert.Redirect;
using TqkLibrary.WinDivert.Redirect.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Covers the parts of IPv6 redirect that are pure logic. The packet path itself needs the driver
// and a live network, so it is verified by running the tool; what is testable here is the state
// that path depends on — the NAT key, the fall back decision, and the address formats the proxy
// protocols are handed.
public class Ipv6RoutingTests
{
    private static Outbound Proxy(Ipv6Support support = Ipv6Support.Auto, OutboundKind kind = OutboundKind.Socks5)
        => new Outbound { Id = Guid.NewGuid(), Name = "p", Kind = kind, Ipv6Support = support };

    // ---- NAT table -----------------------------------------------------------------------

    [Fact]
    public void Nat_keeps_v4_and_v6_flows_on_the_same_port_apart()
    {
        // Windows hands out source ports per family, so both of these can exist at once. Before
        // the family was part of the key the second Upsert overwrote the first and one of the two
        // connections was relayed to the other's destination.
        var nat = new NatTable();
        nat.Upsert(new NatEntry(1, 6, IPAddress.Parse("192.168.1.5"), 50000, IPAddress.Parse("93.184.216.34"), 443, 7, 0));
        nat.Upsert(new NatEntry(2, 6, IPAddress.Parse("2402:800::5"), 50000, IPAddress.Parse("2606:4700::1111"), 443, 7, 0));

        NatEntry? v4 = nat.Find(6, 50000, isIpv6: false);
        NatEntry? v6 = nat.Find(6, 50000, isIpv6: true);

        Assert.Equal(2, nat.Count);
        Assert.Equal("93.184.216.34", v4!.OriginalDestinationAddress.ToString());
        Assert.Equal("2606:4700::1111", v6!.OriginalDestinationAddress.ToString());
        Assert.Equal(1u, v4.ProcessId);
        Assert.Equal(2u, v6.ProcessId);
    }

    [Fact]
    public void Nat_lookup_with_the_wrong_family_misses()
    {
        var nat = new NatTable();
        nat.Upsert(new NatEntry(1, 17, IPAddress.Parse("2402:800::5"), 5353, IPAddress.Parse("2001:4860:4860::8888"), 53, 7, 0));

        Assert.Null(nat.Find(17, 5353, isIpv6: false));
        Assert.NotNull(nat.Find(17, 5353, isIpv6: true));
    }

    // ---- outbound capability -------------------------------------------------------------

    [Fact]
    public void Auto_outbound_allows_ipv6_until_one_destination_fails()
    {
        var capability = new OutboundIpv6Capability();
        Outbound outbound = Proxy();

        Assert.True(capability.AllowsIpv6(outbound));
        Assert.True(capability.RecordIpv6Failure(outbound));
        Assert.False(capability.AllowsIpv6(outbound));
        // Only the first failure changes anything; the rest are noise.
        Assert.False(capability.RecordIpv6Failure(outbound));
    }

    [Fact]
    public void A_user_who_said_enabled_is_not_overruled_by_a_failure()
    {
        var capability = new OutboundIpv6Capability();
        Outbound outbound = Proxy(Ipv6Support.Enabled);

        capability.RecordIpv6Failure(outbound);

        Assert.True(capability.AllowsIpv6(outbound));
        Assert.False(capability.HasLearnedFailure(outbound.Id));
    }

    [Fact]
    public void Disabled_outbound_never_takes_ipv6()
    {
        var capability = new OutboundIpv6Capability();

        Assert.False(capability.AllowsIpv6(Proxy(Ipv6Support.Disabled)));
    }

    [Fact]
    public void Socks4_can_never_carry_ipv6_whatever_the_setting_says()
    {
        // The protocol has no address type for it, so this is not a preference.
        var capability = new OutboundIpv6Capability();

        Assert.False(capability.AllowsIpv6(Proxy(Ipv6Support.Enabled, OutboundKind.Socks4)));
    }

    [Fact]
    public void Editing_an_outbound_forgets_what_was_learned()
    {
        var capability = new OutboundIpv6Capability();
        Outbound outbound = Proxy();
        capability.RecordIpv6Failure(outbound);

        capability.Reset(outbound.Id);

        Assert.True(capability.AllowsIpv6(outbound));
    }

    // ---- address formats -------------------------------------------------------------------

    [Fact]
    public void Target_uri_brackets_an_ipv6_literal()
    {
        Uri uri = new UriBuilder("tcp", "2606:4700::1111", 443).Uri;

        Assert.Equal(UriHostNameType.IPv6, uri.HostNameType);
        Assert.Equal("[2606:4700::1111]", uri.Host);
        Assert.Equal(443, uri.Port);
    }

    [Fact]
    public void Socks5_destination_accepts_an_ipv6_target_uri()
    {
        // Regression: Uri.Host keeps the brackets and IPAddress.Parse rejects them, so every IPv6
        // destination used to throw a FormatException on its way into the SOCKS5 request.
        Uri uri = new UriBuilder("tcp", "2606:4700::1111", 443).Uri;

        var dst = new Socks5_DSTADDR(uri);

        Assert.Equal(Socks5_ATYP.IpV6, dst.ATYP);
        Assert.Equal(IPAddress.Parse("2606:4700::1111"), dst.IPAddress);
    }

    [Fact]
    public void Socks5_destination_still_prefers_a_name_when_there_is_one()
    {
        var dst = new Socks5_DSTADDR(new UriBuilder("tcp", "example.com", 443).Uri);

        Assert.Equal(Socks5_ATYP.DomainName, dst.ATYP);
        Assert.Equal("example.com", dst.Domain);
    }
}
