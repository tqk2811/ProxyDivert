using ProxyDivert.Core.Routing;

namespace ProxyDivert.Core.Engine.Interfaces;

/// <summary>
/// Where a router reads the routing table from, once per connection.
/// </summary>
/// <remarks>
/// The resolver is not handed to a router and kept: it is replaced wholesale every time the
/// configuration is saved and every time a process is attached or detached, which on a busy machine
/// is several times a minute. A router that took a <see cref="RoutingPolicyResolver"/> in its
/// constructor would go on routing by the table that happened to be current when the engine
/// started, and the log would say "configuration applied" while nothing about the routing changed.
///
/// Asking for it per connection is also what makes "an edit takes effect on the next connection"
/// true rather than approximately true: a read landing a moment before a swap routes by the
/// previous table, whole, instead of seeing a half-published one.
/// </remarks>
public interface IResolverSource
{
    RoutingPolicyResolver Resolver { get; }
}
