using System;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// Block is a routing decision, not a way out: a blocked connection is closed rather than
/// tunnelled, so there is nothing to build.
/// </summary>
/// <remarks>
/// It gets a builder of its own all the same. Every kind the enum has is then accounted for by a
/// class, and asking for this one says what is actually wrong — a caller that reached here has
/// routed a connection instead of closing it — rather than "unknown outbound kind", which would
/// send whoever reads the log looking for a missing feature.
/// </remarks>
public sealed class BlockOutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.Block;

    // Nothing is built at all, so there is certainly nothing to keep up.
    public bool BuildsManagedSource => false;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
        => throw new InvalidOperationException(
            "Block has no proxy source — the caller must close the connection instead of tunnelling it.");
}
