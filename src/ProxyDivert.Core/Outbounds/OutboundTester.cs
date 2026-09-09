using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Outbounds;

/// <summary>
/// Answers "does this way out work?" by opening a throwaway tunnel through it, without touching the
/// instance live traffic is using.
/// </summary>
/// <remarks>
/// Throwaway is the whole point, and it is why this asks the factory rather than the registry: the
/// registry OWNS an instance and would hand back the one a VPN tunnel is running on, so a failed
/// test would take a working tunnel down with it. What the factory builds belongs to nobody, which
/// also means its disposal is this class's business — a VPN test starts a wireproxy subprocess of
/// its own, and without the DisposeAsync below every press of Test left another one holding a SOCKS
/// port and a WireGuard session for as long as the application ran.
///
/// It was a static method on the engine, which meant a test of the button had to start a driver.
/// Nothing about checking a proxy has anything to do with redirecting packets.
/// </remarks>
public sealed class OutboundTester
{
    private readonly OutboundSourceFactory _factory;
    private readonly ILoggerFactory _loggerFactory;

    public OutboundTester(OutboundSourceFactory factory, ILoggerFactory loggerFactory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Returns null when the outbound reached <paramref name="testHost"/>, or the reason it did not.
    /// </summary>
    public async Task<string?> TestAsync(
        Outbound outbound, string? wireProxyPath = null,
        string testHost = "example.com", int testPort = 80, CancellationToken ct = default)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        if (outbound.IsBlocked) return "Block never connects anywhere.";

        IOutboundInstance? instance = null;
        IConnectSource? tunnel = null;
        try
        {
            instance = _factory.Create(outbound, _loggerFactory, wireProxyPath);
            tunnel = await instance.Source.GetConnectSourceAsync(Guid.NewGuid(), ct).ConfigureAwait(false);
            await tunnel.ConnectAsync(new UriBuilder("tcp", testHost, testPort).Uri, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            try { tunnel?.Dispose(); } catch { }
            // After the tunnel, and whatever happened above: this is what kills the subprocess.
            if (instance is not null)
            {
                try { await instance.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }
}
