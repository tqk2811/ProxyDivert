using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.VpnClient.IpStack.Udp;
using TqkLibrary.VpnClient.Tunnels;

namespace ProxyDivert.Core.Vpn.Client;

// The UDP egress of a SOCKS5 UDP ASSOCIATE, carried by the tunnel's own userspace UDP socket.
//
// This is what the wireproxy engine cannot do — its SOCKS5 is TCP-only, so UDP through that
// outbound is blocked rather than tunnelled. Here the datagrams ride the same stack the TCP
// connections do, so a QUIC or DNS flow routed to this outbound genuinely leaves through the VPN.
//
// The socket is connectionless: one source serves every destination the client aims at, which is
// exactly the shape SOCKS5 UDP ASSOCIATE expects.
internal sealed class VpnClientUdpAssociateSource : IUdpAssociateSource
{
    private readonly VpnTunnel _tunnel;
    private readonly ILogger? _logger;
    private UdpConnection? _socket;
    private bool _disposed;

    public VpnClientUdpAssociateSource(VpnTunnel tunnel, ILogger? logger = null)
    {
        _tunnel = tunnel;
        _logger = logger;
    }

    public IPEndPoint? RelayEndPoint { get; private set; }

    public IPEndPoint? LocalEndPoint { get; private set; }

    public Task<IPEndPoint> AssociateAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VpnClientUdpAssociateSource));
        if (_socket is not null) throw new InvalidOperationException($"{nameof(AssociateAsync)} was already called.");

        _socket = _tunnel.Stack.BindUdp();
        // There is no OS socket to report: the SOCKS5 server builds the address it hands the client
        // itself and never reads these. The bound port is reported so a log line can be followed.
        LocalEndPoint = new IPEndPoint(IPAddress.Any, _socket.LocalPort);
        RelayEndPoint = LocalEndPoint;
        _logger?.LogDebug("udp associate bound to port {Port} inside the tunnel", _socket.LocalPort);
        return Task.FromResult(RelayEndPoint);
    }

    public Task SendAsync(IPEndPoint destination, byte[] payload, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (offset < 0 || count < 0 || offset + count > payload.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (_disposed) throw new ObjectDisposedException(nameof(VpnClientUdpAssociateSource));
        if (_socket is null) throw new InvalidOperationException($"Call {nameof(AssociateAsync)} first.");

        _socket.SendTo(destination.Address, (ushort)destination.Port, payload.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public async Task<UdpAssociateDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VpnClientUdpAssociateSource));
        if (_socket is null) throw new InvalidOperationException($"Call {nameof(AssociateAsync)} first.");

        UdpReceiveResult result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return new UdpAssociateDatagram(new IPEndPoint(result.RemoteAddress, result.RemotePort), result.Data);
    }

    public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A UDP ASSOCIATE source has no stream — use SendAsync and ReceiveAsync.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_socket is not null)
        {
            _tunnel.Stack.UnbindUdp(_socket.LocalPort);
            _socket = null;
        }
    }
}
