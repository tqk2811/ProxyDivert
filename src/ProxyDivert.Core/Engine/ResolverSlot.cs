using System;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Routing;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The one place the current routing table lives during a run, and the only thing that changes
/// about a run once it has started.
/// </summary>
/// <remarks>
/// It exists as an object of its own so that the routers can be built with the same run they belong
/// to. A router needs to READ the table the engine keeps swapping, the engine's run holds the
/// routers — pointing both at this small slot breaks the circle without giving anyone a mutable
/// back-reference to the run.
/// </remarks>
internal sealed class ResolverSlot : IResolverSource
{
    // Volatile because it is written under the engine's lock and read without it, from the relay
    // threads. See IResolverSource for why a read never sees a partly published table.
    private volatile RoutingPolicyResolver _resolver;

    public ResolverSlot(RoutingPolicyResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public RoutingPolicyResolver Resolver => _resolver;

    public void Use(RoutingPolicyResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }
}
