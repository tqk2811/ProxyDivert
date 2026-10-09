using System;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.ViewModels;
using TqkLibrary.Proxy.ProxySources;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The two anti-DPI switches — TLS (the ClientHello's SNI) and CONNECT (the name sent to a proxy):
// which outbounds take each, what their sources are given, and when a change rebuilds the instance.
public class AntiDpiOutboundTests
{
    private static Outbound Make(OutboundKind kind, string? url, bool tls, bool connect = false, int chunk = Outbound.DefaultAntiDpiChunkSize)
        => new Outbound
        {
            Id = Guid.NewGuid(), Name = "x", Kind = kind, Url = url,
            AntiDpiTls = tls, AntiDpiConnect = connect, AntiDpiChunkSize = chunk,
        };

    private static IOutboundInstance Build(IOutboundSourceBuilder builder, Outbound outbound)
        => builder.Build(outbound, new OutboundBuildContext { Signature = OutboundSignature.Of(outbound) });

    [Fact]
    public void Defaults_AreOffWithTwoBytes()
    {
        Outbound direct = Outbound.CreateDirect();

        Assert.False(direct.AntiDpiTls);
        Assert.False(direct.AntiDpiConnect);
        Assert.Equal(2, direct.AntiDpiChunkSize);
        Assert.Equal((0, 0), (direct.EffectiveTlsChunkSize, direct.EffectiveConnectChunkSize));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void Direct_HandsTheTlsSizeToItsSource(bool tls, int expected)
    {
        var source = (LocalProxySource)Build(new DirectOutboundBuilder(), Make(OutboundKind.Direct, null, tls, connect: true)).Source;

        Assert.Equal(expected, source.TlsHandshakeChunkSize);
    }

    [Fact]
    public void Direct_HasNoConnectToSplit()
    {
        Assert.Equal(0, Make(OutboundKind.Direct, null, false, connect: true).EffectiveConnectChunkSize);
    }

    [Theory]
    [InlineData(true, false, 3, 0)]
    [InlineData(false, true, 0, 3)]
    [InlineData(true, true, 3, 3)]
    public void Proxies_TakeEachSwitchOnItsOwn(bool tls, bool connect, int expectedTls, int expectedConnect)
    {
        var http = (HttpProxySource)Build(new HttpProxyOutboundBuilder(), Make(OutboundKind.HttpProxy, "http://127.0.0.1:8080", tls, connect, 3)).Source;
        var socks5 = (Socks5ProxySource)Build(new Socks5OutboundBuilder(), Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080", tls, connect, 3)).Source;
        var socks4 = (Socks4ProxySource)Build(new Socks4OutboundBuilder(), Make(OutboundKind.Socks4, "socks4://127.0.0.1:1080", tls, connect, 3)).Source;

        Assert.Equal((expectedConnect, expectedTls), (http.ConnectRequestChunkSize, http.TlsHandshakeChunkSize));
        Assert.Equal((expectedConnect, expectedTls), (socks5.ConnectRequestChunkSize, socks5.TlsHandshakeChunkSize));
        Assert.Equal((expectedConnect, expectedTls), (socks4.ConnectRequestChunkSize, socks4.TlsHandshakeChunkSize));
    }

    [Fact]
    public void AHandEditedZero_StillLeavesTheSwitchOn()
    {
        Assert.Equal(1, Make(OutboundKind.Direct, null, true, chunk: 0).EffectiveTlsChunkSize);
    }

    [Theory]
    [InlineData(OutboundKind.Vpn)]
    [InlineData(OutboundKind.Ssh)]
    [InlineData(OutboundKind.Block)]
    public void KindsWithNoNameOnTheWire_IgnoreBothSwitches(OutboundKind kind)
    {
        Outbound outbound = Make(kind, null, true, true);

        Assert.Equal((0, 0), (outbound.EffectiveTlsChunkSize, outbound.EffectiveConnectChunkSize));
    }

    [Fact]
    public void Signature_ChangesWithEachSwitchAndTheSize()
    {
        Outbound outbound = Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080", false);
        string off = OutboundSignature.Of(outbound);

        outbound.AntiDpiTls = true;
        string tls = OutboundSignature.Of(outbound);
        outbound.AntiDpiConnect = true;
        string both = OutboundSignature.Of(outbound);
        outbound.AntiDpiChunkSize = 4;
        string four = OutboundSignature.Of(outbound);

        Assert.Equal(4, new[] { off, tls, both, four }.Distinct().Count());
    }

    [Fact]
    public void Normalize_KeepsThemOnDirect_AndClearsThemOnBlock()
    {
        AppConfig config = AppConfig.CreateDefault();
        foreach (Outbound outbound in config.Outbounds) outbound.AntiDpiTls = outbound.AntiDpiConnect = true;

        config.Normalize();

        Outbound direct = config.Outbounds.Find(o => o.Id == Outbound.DirectId)!;
        Outbound block = config.Outbounds.Find(o => o.Id == Outbound.BlockId)!;
        Assert.True(direct.AntiDpiTls);
        Assert.False(block.AntiDpiTls || block.AntiDpiConnect);
    }

    [Fact]
    public void Normalize_PutsTheDefaultBackForASizeBelowOne()
    {
        AppConfig config = AppConfig.CreateDefault();
        Outbound direct = config.Outbounds.Find(o => o.Id == Outbound.DirectId)!;
        direct.AntiDpiChunkSize = 0;

        config.Normalize();

        Assert.Equal(Outbound.DefaultAntiDpiChunkSize, direct.AntiDpiChunkSize);
    }

    [Fact]
    public void BuiltInDirectRow_TakesTheTlsSwitch_ButNotConnectNorAnythingElse()
    {
        var row = new OutboundRowViewModel(Outbound.CreateDirect());

        row.AntiDpiTls = true;
        row.AntiDpiConnect = true;
        row.AntiDpiChunkSize = 5;
        row.Url = "http://not.allowed:1";

        Assert.True(row.Model.AntiDpiTls);
        Assert.False(row.Model.AntiDpiConnect);
        Assert.Equal(5, row.Model.AntiDpiChunkSize);
        Assert.Null(row.Model.Url);
    }

    [Fact]
    public void VpnRow_RefusesBoth_AndAnyRowRefusesASizeBelowOne()
    {
        var vpn = new OutboundRowViewModel(Make(OutboundKind.Vpn, "C:/none.conf", false));
        vpn.AntiDpiTls = true;
        vpn.AntiDpiConnect = true;
        Assert.False(vpn.Model.AntiDpiTls || vpn.Model.AntiDpiConnect);

        var proxy = new OutboundRowViewModel(Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080", true));
        proxy.AntiDpiConnect = true;
        proxy.AntiDpiChunkSize = 0;
        Assert.True(proxy.Model.AntiDpiConnect);
        Assert.Equal(2, proxy.Model.AntiDpiChunkSize);
    }
}
