using System;
using System.IO;
using System.Threading.Tasks;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What a built outbound tells its owner about itself.
//
// The supervisor used to work this out by pattern-matching the concrete source, so every new way of
// running a VPN meant editing the supervisor as well as the factory. Now it asks the instance, and
// the builder that chose the engine is the one that answers.
//
// Nothing here dials, spawns wireproxy, or needs elevation: building an instance is inert, and
// these tests never start one.
public class OutboundInstanceTests
{
    [Fact]
    public async Task AVpnOnWireProxy_ComesWithATunnelToHoldOpen()
    {
        // A .conf that already has a [Socks5] section is handed to wireproxy untouched, so this
        // needs no WireGuard keys to be a valid outbound.
        string path = WriteTempConf("[Interface]\n[Socks5]\nBindAddress = 127.0.0.1:25344\n");
        // An empty file is enough: the source refuses to be built at all when the binary is not
        // there, and nothing here ever starts it.
        string binary = WriteTempFile(".exe", string.Empty);
        try
        {
            OutboundSourceFactory factory = OutboundSourceFactory.CreateDefault();
            IOutboundInstance instance = factory.Create(Vpn(path), loggerFactory: null, wireProxyPath: binary);
            try
            {
                // The same object, not something wrapped around it. wireproxy used to be made
                // watchable by an adapter this application owned, which is what let the supervisor's
                // idea of a tunnel drift away from the source that actually is one.
                Assert.Same(instance.Source, instance.Tunnel);
            }
            finally
            {
                await instance.DisposeAsync();
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(binary); } catch { }
        }
    }

    // A proxy is a place to send a connection, not a session anyone holds up, so the keeper must be
    // told there is nothing here rather than being handed something it would supervise forever.
    [Fact]
    public async Task ASocks5Outbound_HasNothingToHoldOpen()
    {
        OutboundSourceFactory factory = OutboundSourceFactory.CreateDefault();
        IOutboundInstance instance = factory.Create(
            new Outbound
            {
                Id = Guid.NewGuid(),
                Name = "proxy",
                Kind = OutboundKind.Socks5,
                Url = "socks5://127.0.0.1:1080",
            },
            loggerFactory: null, wireProxyPath: null);

        try
        {
            Assert.Null(instance.Tunnel);
        }
        finally
        {
            await instance.DisposeAsync();
        }
    }

    // Block is a routing decision, not a way out. It has a builder of its own so that reaching here
    // says what is actually wrong — a connection was routed instead of closed — rather than
    // "unknown outbound kind", which reads like a missing feature.
    [Fact]
    public void Block_SaysWhyItHasNoWayOut()
    {
        OutboundSourceFactory factory = OutboundSourceFactory.CreateDefault();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => factory.Create(
                new Outbound { Id = Outbound.BlockId, Name = "Block", Kind = OutboundKind.Block },
                loggerFactory: null, wireProxyPath: null));

        Assert.Contains("close the connection", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The same question has two answers, and they must not drift apart.
    //
    // The routing path asks Outbound.SupportsUdp, which works it out from the configuration, because
    // it answers once per datagram and building a VPN is what dials it. The instance answers from
    // what was actually built. If those disagree, a datagram is routed through an outbound whose
    // source then refuses it and the datagram is dropped with nothing saying why — so the agreement
    // is pinned here instead, where it fails in the build rather than at three in the morning.
    [Theory]
    [InlineData(OutboundKind.Direct, null)]
    [InlineData(OutboundKind.HttpProxy, "http://127.0.0.1:8080")]
    [InlineData(OutboundKind.Socks4, "socks4://127.0.0.1:1080")]
    [InlineData(OutboundKind.Socks5, "socks5://127.0.0.1:1080")]
    // An in-process tunnel: its own IP stack, so it really does carry datagrams.
    [InlineData(OutboundKind.Vpn, "sstp://vpn.example.com")]
    public async Task WhetherAnOutboundCarriesUdp_ReadsTheSameOffTheModelAndOffTheBuiltInstance(
        OutboundKind kind, string? url)
    {
        var outbound = new Outbound { Id = Guid.NewGuid(), Name = "way out", Kind = kind, Url = url };

        OutboundSourceFactory factory = OutboundSourceFactory.CreateDefault();
        IOutboundInstance instance = factory.Create(outbound, loggerFactory: null, wireProxyPath: null);
        try
        {
            Assert.Equal(outbound.SupportsUdp, instance.SupportsUdp);
        }
        finally
        {
            await instance.DisposeAsync();
        }
    }

    // The wireproxy half of the same rule, which needs a real file to build and so cannot ride the
    // theory above. Its SOCKS5 listener is TCP-only, so both answers have to be false.
    [Fact]
    public async Task AVpnOnWireProxy_SaysItCarriesNoUdp_OnBothSides()
    {
        string path = WriteTempConf("[Interface]\n[Socks5]\nBindAddress = 127.0.0.1:25345\n");
        string binary = WriteTempFile(".exe", string.Empty);
        try
        {
            Outbound outbound = Vpn(path);
            OutboundSourceFactory factory = OutboundSourceFactory.CreateDefault();
            IOutboundInstance instance = factory.Create(outbound, loggerFactory: null, wireProxyPath: binary);
            try
            {
                Assert.False(outbound.SupportsUdp);
                Assert.Equal(outbound.SupportsUdp, instance.SupportsUdp);
            }
            finally
            {
                await instance.DisposeAsync();
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(binary); } catch { }
        }
    }

    private static Outbound Vpn(string configPath) => new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "vpn",
        Kind = OutboundKind.Vpn,
        Url = configPath,
    };

    private static string WriteTempConf(string text) => WriteTempFile(".conf", text);

    private static string WriteTempFile(string extension, string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"pd-instance-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text);
        return path;
    }
}
