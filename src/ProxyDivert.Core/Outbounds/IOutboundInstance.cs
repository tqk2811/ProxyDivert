using System;
using System.Threading.Tasks;
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
    /// The same source seen as something that can be brought up ahead of the first request and then
    /// watched, or null when this way out has nothing to hold open.
    /// </summary>
    /// <remarks>
    /// It is the source itself answering, not a decision made here: a way out that holds a
    /// subprocess or a session says so by implementing <see cref="IManagedProxySource"/>. This
    /// member is kept even though it is a cast, because a supervisor asking "is there anything to
    /// keep up?" is the question the instance exists to answer — the alternative has every owner
    /// writing the cast, which is one step from writing a type test again.
    /// </remarks>
    IManagedProxySource? Tunnel { get; }

    /// <summary>
    /// Whether this way out can carry datagrams, as built.
    /// </summary>
    /// <remarks>
    /// The routing path does NOT ask this, and that is on purpose: it answers per datagram, before
    /// anything has been built, and building a VPN is what dials it — so <see cref="Outbound"/>
    /// works the same answer out from the configuration instead. Two answers to one question is a
    /// place they can drift apart, which is why a test walks every kind and pins them equal. This
    /// one is here for whoever is already holding an instance, so that reading a capability off a
    /// built source does not mean casting it.
    /// </remarks>
    bool SupportsUdp { get; }

    /// <summary>
    /// Turns IPv6 off (or back on) for this instance: the source then stops handing IPv6 addresses
    /// out on its own — for Direct that means name lookups return A records only, which is what
    /// makes "no IPv6 out there, use IPv4" actually happen. A no-op for a way out whose answer is
    /// fixed, such as SOCKS4, which has no IPv6 in the protocol at all.
    /// </summary>
    void SetIpv6Support(bool supported);
}
