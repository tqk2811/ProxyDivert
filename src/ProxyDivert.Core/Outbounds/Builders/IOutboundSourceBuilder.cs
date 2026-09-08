using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// Builds the live instance of one kind of outbound. One implementation per
/// <see cref="OutboundKind"/>, so adding a way out is adding a class rather than editing a switch
/// in the middle of a 350-line factory.
/// </summary>
public interface IOutboundSourceBuilder
{
    /// <summary>The kind this builder answers for. Exactly one builder per kind.</summary>
    OutboundKind Kind { get; }

    /// <summary>
    /// True when <see cref="Build"/> produces an <see cref="IManagedProxySource"/>: something that
    /// holds a subprocess or a session open, and that the supervisor can therefore dial ahead of
    /// the first request and keep up.
    /// </summary>
    /// <remarks>
    /// Answered without building, because the question is asked of every outbound in the
    /// configuration and building a VPN means dialling it. That is also why the supervisor cannot
    /// just test the instance for the interface, and why it used to test
    /// <c>Kind == OutboundKind.Vpn</c> instead — a rule that lives nowhere near the builders and
    /// would quietly leave the next kept-open kind unsupervised. Stated here, the compiler makes
    /// whoever adds a builder answer it.
    /// </remarks>
    bool BuildsManagedSource { get; }

    /// <summary>
    /// Builds the instance, or throws with a message the user can act on — a missing URL, a
    /// configuration file that is not there. Never caches: what is built here belongs to the caller
    /// from the moment it returns.
    /// </summary>
    IOutboundInstance Build(Outbound outbound, OutboundBuildContext context);
}
