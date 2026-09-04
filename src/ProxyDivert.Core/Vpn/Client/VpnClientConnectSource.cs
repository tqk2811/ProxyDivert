using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TqkLibrary.Proxy.Exceptions;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.VpnClient.Sockets;
using TqkLibrary.VpnClient.Tunnels;

namespace ProxyDivert.Core.Vpn.Client;

// One proxied TCP connection, opened through the VPN tunnel's userspace stack.
//
// The router hands over a destination that is usually a NAME rather than an address — it recovers
// the name from SNI or from what the process resolved — and that is the whole reason this resolves
// through the tunnel rather than through the machine. See InTunnelResolver.
internal sealed class VpnClientConnectSource : IConnectSource
{
    private readonly VpnTunnel _tunnel;
    private readonly InTunnelResolver _resolver;
    private readonly bool _allowIpv6;
    private readonly ILogger? _logger;
    private Stream? _stream;
    private bool _disposed;

    public VpnClientConnectSource(VpnTunnel tunnel, InTunnelResolver resolver, bool allowIpv6, ILogger? logger = null)
    {
        _tunnel = tunnel;
        _resolver = resolver;
        _allowIpv6 = allowIpv6;
        _logger = logger;
    }

    public async Task ConnectAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (address is null) throw new ArgumentNullException(nameof(address));
        if (_disposed) throw new ObjectDisposedException(nameof(VpnClientConnectSource));

        int port = address.Port > 0
            ? address.Port
            : Uri.UriSchemeHttps.Equals(address.Scheme, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

        IPAddress ip;
        try
        {
            ip = await _resolver.ResolveAsync(address.Host, _allowIpv6, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InitConnectSourceFailedException(
                $"Could not resolve '{address.Host}' inside the VPN tunnel: {ex.Message}");
        }

        try
        {
            VpnTcpClient client = await VpnTcpClient
                .ConnectAsync(_tunnel.Stack, ip, (ushort)port, cancellationToken)
                .ConfigureAwait(false);
            _stream = client.GetStream();
            _logger?.LogDebug("connected to {Host}:{Port} ({Ip}) through the tunnel", address.Host, port, ip);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InitConnectSourceFailedException(
                $"Could not reach {address.Host}:{port} ({ip}) through the VPN tunnel: {ex.Message}");
        }
    }

    public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VpnClientConnectSource));
        if (_stream is null) throw new InvalidOperationException($"Call {nameof(ConnectAsync)} first.");
        return Task.FromResult(_stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream?.Dispose();
        _stream = null;
    }
}
