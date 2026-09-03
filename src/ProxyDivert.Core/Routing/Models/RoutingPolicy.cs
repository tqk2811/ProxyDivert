using System;
using System.Collections.Generic;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models;

// An ordered rule list plus the fallbacks used when no rule matches. A process rule points at one
// of these, which is what makes per-process routing possible.
public sealed class RoutingPolicy
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    public List<RoutingRule> Rules { get; set; } = new List<RoutingRule>();

    // Where a connection goes when no rule matches. Defaults to Direct.
    public Guid DefaultOutboundId { get; set; } = Outbound.DirectId;

    public UdpMode UdpMode { get; set; } = UdpMode.Direct;

    // QUIC (UDP/443) is blocked by default so browsers fall back to TCP, which the proxy can
    // actually carry. Turning this off with a UDP-incapable outbound means QUIC either leaks
    // direct or dies, depending on UdpMode.
    public bool BlockQuic { get; set; } = true;

    public override string ToString() => Name;
}
