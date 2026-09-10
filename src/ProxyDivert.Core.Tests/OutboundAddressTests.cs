using System;
using ProxyDivert.Core.Outbounds.Enums;
using ProxyDivert.Core.Outbounds.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using Xunit;

namespace ProxyDivert.Core.Tests;

// One address box holding four different things, read in one place. What it turns out to be is
// decided here; what to do about it stays with the profile reader and the builders.
public class OutboundAddressTests
{
    private static OutboundAddress Read(OutboundKind kind, string url, VpnProtocol protocol = VpnProtocol.Auto)
    {
        Assert.True(
            OutboundAddress.TryParse(kind, url, protocol, out OutboundAddress? address, out string? error),
            $"'{url}' was refused: {error}");
        return address!;
    }

    private static string? Refuse(OutboundKind kind, string? url, VpnProtocol protocol = VpnProtocol.Auto)
    {
        Assert.False(OutboundAddress.TryParse(kind, url, protocol, out OutboundAddress? address, out string? error));
        Assert.Null(address);
        return error;
    }

    // ==== a proxy ====

    [Theory]
    [InlineData(OutboundKind.Socks5, "socks5")]
    [InlineData(OutboundKind.Socks4, "socks4")]
    [InlineData(OutboundKind.HttpProxy, "http")]
    public void ABareHostAndPort_TakesTheSchemeTheOutboundsKindImplies(OutboundKind kind, string scheme)
    {
        OutboundAddress address = Read(kind, "127.0.0.1:1080");

        Assert.Equal(OutboundAddressKind.ProxyEndpoint, address.Kind);
        Assert.Equal(scheme, address.Uri!.Scheme);
        Assert.Equal("127.0.0.1", address.Host);
        Assert.Equal(1080, address.Port);
    }

    [Fact]
    public void ASchemeThatWasTyped_IsKeptRatherThanReplacedByTheKind()
    {
        OutboundAddress address = Read(OutboundKind.HttpProxy, "https://proxy.example.com:8443");

        Assert.Equal("https", address.Uri!.Scheme);
        Assert.Equal(8443, address.Port);
    }

    [Fact]
    public void AnIpv6Proxy_LosesTheBracketsThatOnlyTheUriNeeds()
    {
        OutboundAddress address = Read(OutboundKind.Socks5, "socks5://[::1]:1080");

        Assert.Equal("::1", address.Host);
        Assert.Equal(1080, address.Port);
    }

    // Uri fills in 80 for http and leaves nothing for a scheme it does not know, which is every
    // SOCKS one. A SOCKS proxy with no port cannot be dialled at all, and the connection that used
    // to find that out was one the user was already waiting on.
    [Fact]
    public void ASocksProxyWithNoPort_IsRefusedAtTheBoxRatherThanAtTheFirstConnection()
    {
        string? error = Refuse(OutboundKind.Socks5, "socks5://127.0.0.1");

        Assert.NotNull(error);
        Assert.Contains("port", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnHttpProxyWithNoPort_KeepsTheSchemesOwnPort()
    {
        Assert.Equal(80, Read(OutboundKind.HttpProxy, "http://proxy.example.com").Port);
    }

    [Fact]
    public void AnEmptyBox_IsRefusedWithSomethingToDoAboutIt()
    {
        string? error = Refuse(OutboundKind.Socks5, "   ");

        Assert.NotNull(error);
        Assert.Contains("socks5://", error!, StringComparison.OrdinalIgnoreCase);
    }

    // Direct is the machine's own stack and Block is the absence of one. Neither is a way to
    // somewhere, so there is no address to read and nothing wrong with there not being one.
    [Theory]
    [InlineData(OutboundKind.Direct)]
    [InlineData(OutboundKind.Block)]
    public void TheTwoBuiltIns_WantNoAddressAndSoReportNoProblem(OutboundKind kind)
    {
        Assert.Null(Refuse(kind, "anything at all"));
    }

    // ==== a VPN ====

    [Fact]
    public void AVpnServer_ReadsAsAnAddress()
    {
        OutboundAddress address = Read(OutboundKind.Vpn, "sstp://vpn.example.com:443");

        Assert.Equal(OutboundAddressKind.VpnEndpoint, address.Kind);
        Assert.Equal("vpn.example.com", address.Host);
        Assert.Equal(443, address.Port);
        Assert.Null(address.Hub);
    }

    [Fact]
    public void ASoftEtherAddress_CarriesItsVirtualHub()
    {
        OutboundAddress address = Read(OutboundKind.Vpn, "softether://vpn.example.com:443/VPNGATE");

        Assert.Equal("VPNGATE", address.Hub);
    }

    // A path first, always. Otherwise "D:\vpn\jp.ovpn" typed against a dialled protocol would be
    // dialled as the host "d", and the failure would say nothing about what actually went wrong.
    [Theory]
    [InlineData(@"D:\vpn\jp.ovpn")]
    [InlineData(@"\\server\share\wg0.conf")]
    public void APathTypedAgainstADialledProtocol_IsStillAPath(string path)
    {
        OutboundAddress address = Read(OutboundKind.Vpn, path, VpnProtocol.Sstp);

        Assert.Equal(OutboundAddressKind.VpnConfigFile, address.Kind);
        Assert.Equal(path, address.Path);
    }

    [Fact]
    public void ABareServerIsAnAddressOnlyOnceTheUserHasSaidWhichProtocolTheyMean()
    {
        Assert.Equal(OutboundAddressKind.VpnConfigFile, Read(OutboundKind.Vpn, "219.100.37.1:443").Kind);
        Assert.Equal(
            OutboundAddressKind.VpnEndpoint,
            Read(OutboundKind.Vpn, "219.100.37.1:443", VpnProtocol.Sstp).Kind);
    }

    [Fact]
    public void AVpnIniFile_IsToldApartFromAProvidersConfiguration()
    {
        Assert.Equal(OutboundAddressKind.VpnIniFile, Read(OutboundKind.Vpn, @"D:\vpn\office.vpn").Kind);
        Assert.Equal(OutboundAddressKind.VpnConfigFile, Read(OutboundKind.Vpn, @"D:\vpn\wg0.conf").Kind);
    }

    // ==== what people paste in without meaning to ====

    [Fact]
    public void QuotesFromACopiedPath_AreTakenOff()
    {
        Assert.Equal(@"D:\vpn\jp.ovpn", Read(OutboundKind.Vpn, "\"D:\\vpn\\jp.ovpn\"").Path);
    }

    [Fact]
    public void AnEnvironmentVariableInAPath_IsExpandedOnceHereRatherThanByEveryReader()
    {
        OutboundAddress address = Read(OutboundKind.Vpn, @"%TEMP%\wg0.conf");

        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%TEMP%\wg0.conf"), address.Path);
    }

    // ==== the answer is cached, and the cache follows the boxes it was read from ====

    [Fact]
    public void EditingTheAddress_IsRead()
    {
        var outbound = new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "work",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
        };
        Assert.Equal(1080, outbound.Address!.Port);

        outbound.Url = "socks5://127.0.0.1:9050";

        Assert.Equal(9050, outbound.Address!.Port);
    }

    // The same text means two different things depending on the protocol box beside it, so that box
    // has to throw the cached answer away as well.
    [Fact]
    public void PickingTheProtocolByHand_ChangesWhatTheSameTextMeans()
    {
        var outbound = new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "vpn",
            Kind = OutboundKind.Vpn,
            Url = "219.100.37.1:443",
        };
        Assert.Equal(OutboundAddressKind.VpnConfigFile, outbound.Address!.Kind);

        outbound.VpnProtocol = VpnProtocol.Sstp;

        Assert.Equal(OutboundAddressKind.VpnEndpoint, outbound.Address!.Kind);
    }

    [Fact]
    public void TurningARowIntoDirect_LeavesNoAddressBehind()
    {
        var outbound = new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "work",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
        };
        Assert.NotNull(outbound.Address);

        outbound.Kind = OutboundKind.Direct;

        Assert.Null(outbound.Address);
    }

    // ==== the command line, which has an address and no kind ====

    [Theory]
    [InlineData("http://127.0.0.1:8080", OutboundKind.HttpProxy)]
    [InlineData("HTTPS://127.0.0.1:8080", OutboundKind.HttpProxy)]
    [InlineData("socks4a://127.0.0.1:1080", OutboundKind.Socks4)]
    [InlineData("socks://127.0.0.1:1080", OutboundKind.Socks5)]
    [InlineData("socks5://127.0.0.1:1080", OutboundKind.Socks5)]
    public void TheSchemeSaysWhichProxyItIs(string url, OutboundKind expected)
    {
        Assert.True(OutboundAddress.TryReadProxyKind(url, out OutboundKind kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("127.0.0.1:1080")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("")]
    public void SomethingThatNamesNoProxy_IsRefusedRatherThanGuessed(string url)
    {
        Assert.False(OutboundAddress.TryReadProxyKind(url, out _));
    }
}
