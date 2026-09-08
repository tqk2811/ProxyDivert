using System;
using System.Threading.Tasks;
using ProxyDivert.Core.Vpn;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Outbounds;

/// <summary>
/// What a builder returns: the source it made, plus the few things its owner needs to know about
/// it without asking what type it is.
/// </summary>
/// <remarks>
/// The IPv6 switch arrives as a delegate rather than being worked out here by testing the source's
/// type. Every concrete source exposes it as a settable property but <see cref="IProxySource"/>
/// only gets it, so this used to be a switch over four concrete classes in the factory — silently
/// wrong for a fifth. The builder that made the source is the one place that knows it by name, so
/// it hands the setter over and the compiler checks it.
/// </remarks>
public sealed class OutboundInstance : IOutboundInstance
{
    private readonly Action<bool>? _setIpv6Support;

    /// <param name="setIpv6Support">
    /// Null when the answer cannot be changed after building — SOCKS4 has no IPv6 at all, and a
    /// wireproxy tunnel takes the setting when its subprocess is configured, not afterwards.
    /// </param>
    public OutboundInstance(
        Guid outboundId,
        string signature,
        IProxySource source,
        IKeptTunnel? tunnel = null,
        Action<bool>? setIpv6Support = null)
    {
        OutboundId = outboundId;
        Signature = signature ?? throw new ArgumentNullException(nameof(signature));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Tunnel = tunnel;
        _setIpv6Support = setIpv6Support;
    }

    public Guid OutboundId { get; }

    public string Signature { get; }

    public IProxySource Source { get; }

    public IKeptTunnel? Tunnel { get; }

    public void SetIpv6Support(bool supported) => _setIpv6Support?.Invoke(supported);

    /// <remarks>
    /// Awaited rather than blocked on. Releasing a VPN instance means putting a tunnel down and
    /// waiting for the driver to finish; the caller that used to block on that was the reason the
    /// source could only give it five seconds before walking away.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try { await Source.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
