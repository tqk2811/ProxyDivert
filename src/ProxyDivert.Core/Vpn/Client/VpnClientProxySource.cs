using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.VpnClient.Tunnels;
using DriverState = TqkLibrary.VpnClient.Drivers.Core.Enums.VpnConnectionState;

namespace ProxyDivert.Core.Vpn.Client;

/// <summary>
/// A VPN tunnel dialled by TqkLibrary.VpnClient inside this process, offered to the router as an
/// ordinary way out.
/// </summary>
/// <remarks>
/// This is the second VPN engine, next to wireproxy, and it differs in the two ways that matter to
/// the router. It owns a whole userspace IP stack rather than borrowing a SOCKS5 listener, so UDP
/// really goes through the tunnel instead of being blocked; and there is no external binary, so the
/// protocols with no wireproxy equivalent — OpenVPN, SSTP, L2TP/IPsec, IKEv2, SoftEther — become
/// available at all.
///
/// The tunnel is dialled once and held. <see cref="IKeptTunnel"/> is how the engine's supervisor
/// keeps it that way; the lazy start in <see cref="GetConnectSourceAsync"/> is only there so the
/// source still works on its own, which is what the Outbounds tab's Test button uses.
/// </remarks>
public sealed class VpnClientProxySource : IProxySource, IKeptTunnel, IDisposable
{
    private readonly VpnProfile _profile;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private VpnTunnel? _tunnel;
    private InTunnelResolver? _resolver;
    private TaskCompletionSource<string>? _down;
    private bool _allowIpv6;
    private volatile bool _disposed;

    public VpnClientProxySource(VpnProfile profile, bool allowIpv6 = true, ILoggerFactory? loggerFactory = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _allowIpv6 = allowIpv6;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<VpnClientProxySource>();
    }

    /// <summary>
    /// Always true: the tunnel carries datagrams over its own stack, so a SOCKS5 UDP ASSOCIATE
    /// through this outbound reaches the destination instead of being downgraded to Block.
    /// </summary>
    public bool IsSupportUdp => true;

    /// <summary>
    /// True only when the user allows IPv6 AND the tunnel actually obtained a global IPv6. Reporting
    /// it otherwise would hand the router an address the tunnel has no route for, which fails later
    /// and less clearly than not offering it.
    /// </summary>
    public bool IsSupportIpv6
    {
        get => _allowIpv6 && _tunnel?.AssignedAddressV6 is not null;
        set => _allowIpv6 = value;
    }

    /// <summary>The stack opens connections, it does not accept them, and the tunnel address is not
    /// reachable from the internet anyway.</summary>
    public bool IsSupportBind => false;

    public bool IsRunning => _tunnel?.IsUp == true;

    public string Endpoint
        => _tunnel is VpnTunnel tunnel
            ? $"{tunnel.ProtocolName} as {tunnel.AssignedAddress}"
            : $"{_profile.Protocol} (not connected)";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsUsable(_tunnel)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsUsable(_tunnel)) return;

            await DropAsync().ConfigureAwait(false);

            _logger?.LogInformation("dialling {Profile}", _profile);
            VpnTunnel tunnel = await DialAsync(cancellationToken).ConfigureAwait(false);

            // Armed before the tunnel is published, so a link that dies during this very method is
            // still reported rather than silently lost.
            var down = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            tunnel.StateChanged += state =>
            {
                if (state == DriverState.Disconnected)
                    down.TrySetResult("the VPN driver gave up re-establishing the tunnel");
            };

            _tunnel = tunnel;
            _down = down;
            _resolver = new InTunnelResolver(tunnel, _loggerFactory?.CreateLogger<InTunnelResolver>());

            _logger?.LogInformation(
                "{Protocol} tunnel up as {Address} (dns {Dns}, mtu {Mtu})",
                tunnel.ProtocolName, tunnel.AssignedAddress,
                tunnel.AssignedDns?.ToString() ?? "none", tunnel.Mtu);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Completes only when the driver has stopped trying. Everything short of that — a dropped link
    /// the driver is re-establishing with its own backoff — is its business, and tearing the tunnel
    /// down in the middle of that would just be racing it.
    /// </summary>
    public async Task<string> WaitUntilDownAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<string>? down = _down;
        if (down is null) return "the tunnel was never started";

        // A driver that gave up before anyone subscribed leaves nothing to wait for.
        if (_tunnel?.State == DriverState.Disconnected)
            return "the VPN driver gave up re-establishing the tunnel";

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            Task first = await Task.WhenAny(down.Task, cancelled.Task).ConfigureAwait(false);
            return ReferenceEquals(first, down.Task) ? await down.Task.ConfigureAwait(false) : "cancelled";
        }
    }

    public async Task<IConnectSource> GetConnectSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        (VpnTunnel tunnel, InTunnelResolver resolver) = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        return new VpnClientConnectSource(
            tunnel, resolver, IsSupportIpv6, _loggerFactory?.CreateLogger<VpnClientConnectSource>());
    }

    public async Task<IUdpAssociateSource> GetUdpAssociateSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        (VpnTunnel tunnel, _) = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        return new VpnClientUdpAssociateSource(
            tunnel, _loggerFactory?.CreateLogger<VpnClientUdpAssociateSource>());
    }

    public Task<IBindSource> GetBindSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "A VPN tunnel opens connections but cannot accept them: the address it is given is private "
            + "to the tunnel and not reachable from the internet.");

    private async Task<(VpnTunnel Tunnel, InTunnelResolver Resolver)> ReadyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await StartAsync(cancellationToken).ConfigureAwait(false);

        VpnTunnel? tunnel = _tunnel;
        InTunnelResolver? resolver = _resolver;
        if (tunnel is null || resolver is null)
            throw new InvalidOperationException($"The {_profile.Protocol} tunnel is not up.");

        return (tunnel, resolver);
    }

    private Task<VpnTunnel> DialAsync(CancellationToken cancellationToken)
    {
        var options = new VpnTunnelOptions
        {
            // Asking for IPv6 costs nothing when the server has none, and a tunnel that does carry a
            // global IPv6 is what lets IsSupportIpv6 ever be true.
            EnableIpv6 = _allowIpv6,
            LoggerFactory = _loggerFactory,
            SoftEtherWatermarkPath = _profile.SoftEtherWatermarkPath,
        };

        return _profile.Protocol switch
        {
            VpnProtocol.WireGuard =>
                VpnDialer.ConnectWireGuardAsync(Required(_profile.ConfigPath, "config file"), options, cancellationToken),

            VpnProtocol.OpenVpn =>
                VpnDialer.ConnectOpenVpnAsync(
                    Required(_profile.ConfigPath, "config file"), _profile.Username, _profile.Password,
                    options, cancellationToken),

            VpnProtocol.Sstp =>
                VpnDialer.ConnectSstpAsync(
                    Required(_profile.Host, "server"), _profile.Port,
                    Required(_profile.Username, "user name"), Required(_profile.Password, "password"),
                    options, cancellationToken),

            VpnProtocol.L2tpIpsec =>
                VpnDialer.ConnectL2tpIpsecAsync(
                    Required(_profile.Host, "server"),
                    Required(_profile.Username, "user name"), Required(_profile.Password, "password"),
                    Required(_profile.PreSharedKey, "pre-shared key"),
                    options, cancellationToken),

            VpnProtocol.Ikev2 =>
                VpnDialer.ConnectIkev2Async(
                    Required(_profile.Host, "server"), Required(_profile.PreSharedKey, "pre-shared key"),
                    // Both empty means PSK-only authentication, which is a normal way to set IKEv2 up.
                    _profile.Username, _profile.Password,
                    options, cancellationToken),

            VpnProtocol.SoftEther =>
                VpnDialer.ConnectSoftEtherAsync(
                    Required(_profile.Host, "server"), _profile.Port,
                    Required(_profile.Username, "user name"), Required(_profile.Password, "password"),
                    Required(_profile.Hub, "virtual hub"),
                    options, cancellationToken),

            _ => throw new NotSupportedException(
                $"{_profile.Protocol} is not dialled in this process. A WireGuard .conf runs on wireproxy "
                + "unless the protocol is set to WireGuard explicitly."),
        };
    }

    private static string Required(string? value, string what)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"This VPN outbound needs a {what}.")
            : value!;

    // Reconnecting counts as usable: the driver is mending the link and the tunnel object stays the
    // one to use. Only Disconnected means it is finished.
    private static bool IsUsable(VpnTunnel? tunnel)
        => tunnel is not null && tunnel.State != DriverState.Disconnected;

    private async Task DropAsync()
    {
        VpnTunnel? tunnel = _tunnel;
        _tunnel = null;
        _down = null;
        _resolver?.Dispose();
        _resolver = null;

        if (tunnel is null) return;
        try { await tunnel.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogDebug(ex, "tearing down the previous tunnel failed"); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VpnClientProxySource));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The factory disposes sources synchronously, so the teardown is waited on with a bound
        // rather than left to finish whenever: a tunnel still sending on a stopped engine would be
        // exactly the leak this outbound exists to prevent.
        try { DropAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        _gate.Dispose();
    }
}
