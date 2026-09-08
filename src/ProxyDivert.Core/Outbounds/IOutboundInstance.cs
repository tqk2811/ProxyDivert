using System;
using System.Threading.Tasks;
using ProxyDivert.Core.Vpn;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Outbounds;

/// <summary>
/// One outbound, built. This is what the application passes around instead of a bare
/// <see cref="IProxySource"/>, and it exists so that "who may throw this away" has an answer.
/// </summary>
/// <remarks>
/// A source is a factory of tunnels rather than a connection, and for a VPN the instance IS the
/// tunnel — a wireproxy subprocess, or a driver holding a session with a provider. Three different
/// objects used to be able to dispose one behind each other's backs, so everything an owner has to
/// decide is on the instance itself: what it was built from (<see cref="Signature"/>), whether it
/// is something that can be held open (<see cref="Tunnel"/>), and how it is released. The owner is
/// <see cref="OutboundRegistry"/>, and nothing else disposes one of these.
/// </remarks>
public interface IOutboundInstance : IAsyncDisposable
{
    Guid OutboundId { get; }

    /// <summary>
    /// What the outbound looked like when this was built, as one comparable string. The owner
    /// compares it against the current configuration to decide whether this instance is still the
    /// right one. See <see cref="OutboundSignature"/>.
    /// </summary>
    string Signature { get; }

    IProxySource Source { get; }

    /// <summary>
    /// The same instance seen as something that can be brought up ahead of the first request and
    /// then watched, or null when this kind of way out has nothing to hold open. Only a VPN has
    /// one — and the two VPN engines answer it differently, which is exactly why the caller must
    /// not go looking at concrete types to find out.
    /// </summary>
    IKeptTunnel? Tunnel { get; }

    /// <summary>
    /// Turns IPv6 off (or back on) for this instance: the source then stops handing IPv6 addresses
    /// out on its own — for Direct that means name lookups return A records only, which is what
    /// makes "no IPv6 out there, use IPv4" actually happen. A no-op for a way out whose answer is
    /// fixed, such as SOCKS4, which has no IPv6 in the protocol at all.
    /// </summary>
    void SetIpv6Support(bool supported);
}
