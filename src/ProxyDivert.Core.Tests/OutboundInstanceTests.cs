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
                Assert.NotNull(instance.Tunnel);
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
