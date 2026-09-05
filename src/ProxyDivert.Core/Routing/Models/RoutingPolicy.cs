using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models;

// A named list of destinations and the one way out they share: "these hosts go through that
// outbound". A process filter names several of these in priority order, which is what makes
// per-process routing possible.
public sealed class RoutingPolicy
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    public List<RoutingRule> Rules { get; set; } = new List<RoutingRule>();

    /// <summary>
    /// Where a connection matching one of these rules goes. One per policy rather than one per
    /// rule: a rule says which destinations belong here, and everything that belongs here leaves
    /// the same way — two ways out means two policies, which the filter can list in the order it
    /// wants them tried.
    /// </summary>
    /// <remarks>
    /// Traffic no policy claims goes Direct, so a policy has nothing to say about what it did not
    /// match — the filter simply moves on to the next policy in its list.
    /// </remarks>
    public Guid OutboundId { get; set; } = Outbound.DirectId;

    public UdpMode UdpMode { get; set; } = UdpMode.Direct;

    // QUIC (UDP/443) is blocked by default so browsers fall back to TCP, which the proxy can
    // actually carry. Turning this off with a UDP-incapable outbound means QUIC either leaks
    // direct or dies, depending on UdpMode.
    public bool BlockQuic { get; set; } = true;

    public override string ToString() => Name;
}
