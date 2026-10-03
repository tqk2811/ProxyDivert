using System;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.ViewModels;
using TqkLibrary.Proxy.ProxySources;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The anti-DPI switch: which outbounds take it, what their sources are given, and when a change
// has to rebuild the instance.
public class AntiDpiOutboundTests
{
    private static Outbound Make(OutboundKind kind, string? url, bool antiDpi, int chunk = Outbound.DefaultAntiDpiChunkSize)
        => new Outbound { Id = Guid.NewGuid(), Name = "x", Kind = kind, Url = url, AntiDpi = antiDpi, AntiDpiChunkSize = chunk };

    private static IOutboundInstance Build(IOutboundSourceBuilder builder, Outbound outbound)
        => builder.Build(outbound, new OutboundBuildContext { Signature = OutboundSignature.Of(outbound) });

    [Fact]
    public void Defaults_AreOffWithTwoBytes()
    {
        Outbound direct = Outbound.CreateDirect();

        Assert.False(direct.AntiDpi);
        Assert.Equal(2, direct.AntiDpiChunkSize);
        Assert.Equal(0, direct.EffectiveAntiDpiChunkSize);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void Direct_HandsTheSizeToItsSource(bool antiDpi, int expected)
    {
        var source = (LocalProxySource)Build(new DirectOutboundBuilder(), Make(OutboundKind.Direct, null, antiDpi)).Source;

        Assert.Equal(expected, source.TlsHandshakeChunkSize);
    }

    [Fact]
    public void Proxies_HandTheSizeToBothConnectAndTls()
    {
        var http = (HttpProxySource)Build(new HttpProxyOutboundBuilder(), Make(OutboundKind.HttpProxy, "http://127.0.0.1:8080", true, 3)).Source;
        var socks5 = (Socks5ProxySource)Build(new Socks5OutboundBuilder(), Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080", true, 3)).Source;
        var socks4 = (Socks4ProxySource)Build(new Socks4OutboundBuilder(), Make(OutboundKind.Socks4, "socks4://127.0.0.1:1080", true, 3)).Source;

        Assert.Equal((3, 3), (http.ConnectRequestChunkSize, http.TlsHandshakeChunkSize));
        Assert.Equal((3, 3), (socks5.ConnectRequestChunkSize, socks5.TlsHandshakeChunkSize));
        Assert.Equal((3, 3), (socks4.ConnectRequestChunkSize, socks4.TlsHandshakeChunkSize));
    }

    [Fact]
    public void AHandEditedZero_StillLeavesTheSwitchOn()
    {
        Assert.Equal(1, Make(OutboundKind.Direct, null, true, 0).EffectiveAntiDpiChunkSize);
    }

    [Theory]
    [InlineData(OutboundKind.Vpn)]
    [InlineData(OutboundKind.Ssh)]
    [InlineData(OutboundKind.Block)]
    public void KindsWithNoNameOnTheWire_IgnoreTheSwitch(OutboundKind kind)
    {
        Assert.Equal(0, Make(kind, null, true).EffectiveAntiDpiChunkSize);
    }

    [Fact]
    public void Signature_ChangesWithTheSwitchAndTheSize()
    {
        Outbound outbound = Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080", false);
        string off = OutboundSignature.Of(outbound);

        outbound.AntiDpi = true;
        string on = OutboundSignature.Of(outbound);
        outbound.AntiDpiChunkSize = 4;
        string four = OutboundSignature.Of(outbound);

        Assert.NotEqual(off, on);
        Assert.NotEqual(on, four);
    }

    [Fact]
    public void Normalize_KeepsItOnDirect_AndClearsItOnBlock()
    {
        AppConfig config = AppConfig.CreateDefault();
        foreach (Outbound outbound in config.Outbounds) outbound.AntiDpi = true;

        config.Normalize();

        Assert.True(config.Outbounds.Find(o => o.Id == Outbound.DirectId)!.AntiDpi);
        Assert.False(config.Outbounds.Find(o => o.Id == Outbound.BlockId)!.AntiDpi);
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
    public void BuiltInDirectRow_TakesTheSwitch_ThoughNothingElse()
    {
        var row = new OutboundRowViewModel(Outbound.CreateDirect());

        row.AntiDpi = true;
        row.AntiDpiChunkSize = 5;
        row.Name = "renamed is fine";
        row.Url = "http://not.allowed:1";

        Assert.True(row.Model.AntiDpi);
        Assert.Equal(5, row.Model.AntiDpiChunkSize);
        Assert.Null(row.Model.Url);
    }

    [Fact]
    public void VpnRow_RefusesTheSwitch_AndAnyRowRefusesASizeBelowOne()
    {
        var vpn = new OutboundRowViewModel(Make(OutboundKind.Vpn, "C:/none.conf", false));
        vpn.AntiDpi = true;
        Assert.False(vpn.Model.AntiDpi);

        var proxy = new OutboundRowViewModel(Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080", true));
        proxy.AntiDpiChunkSize = 0;
        Assert.Equal(2, proxy.Model.AntiDpiChunkSize);
    }
}
