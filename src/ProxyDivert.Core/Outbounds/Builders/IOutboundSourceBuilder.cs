using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

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
    /// Builds the instance, or throws with a message the user can act on — a missing URL, a
    /// configuration file that is not there. Never caches: what is built here belongs to the caller
    /// from the moment it returns.
    /// </summary>
    IOutboundInstance Build(Outbound outbound, OutboundBuildContext context);
}
