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
    /// <summary>
    /// The built-in "Default" policy: always present, first in the Rules tab, one rule that matches
    /// everything. Its name and rules are fixed; only how its traffic leaves is the user's. Where it
    /// stands among a filter's policies is the filter's business, like any other policy.
    /// </summary>
    public static readonly Guid DefaultId = new Guid("6c1f0e8a-3b52-4d7e-9a41-0d5e2f7b8c01");

    public static readonly Guid DefaultRuleId = new Guid("6c1f0e8a-3b52-4d7e-9a41-0d5e2f7b8c02");

    public const string DefaultName = "Default";

    [JsonIgnore]
    public bool IsBuiltIn => Id == DefaultId;

    public static RoutingPolicy CreateDefault() => new RoutingPolicy
    {
        Id = DefaultId,
        Name = DefaultName,
        OutboundId = Outbound.DirectId,
        Rules = { CreateDefaultRule() },
    };

    public static RoutingRule CreateDefaultRule() => new RoutingRule
    {
        Id = DefaultRuleId,
        Matcher = HostMatcherType.Any,
        Pattern = string.Empty,
    };

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

    // Anti-DPI per destination: what these rules match goes out with the outbound's switches
    // turned on (true) or off (false) for this policy alone; null follows the outbound. Only the
    // policy whose rule matched has a say — unlike UdpMode and BlockQuic, which come off the first
    // policy — because the point is to pick the sites, and an outbound that cannot split anything
    // ignores them (see Outbound.WithAntiDpi).
    public bool? AntiDpiTls { get; set; }

    public bool? AntiDpiConnect { get; set; }

    // Bytes of the name per piece for this policy; null follows the outbound.
    public int? AntiDpiChunkSize { get; set; }

    // Secure DNS, process side: a DNS query from a process this policy is applied to, whose name
    // the policy claims, is answered over DNS over HTTPS through the policy's outbound instead of
    // leaving as plain UDP/53. Like anti-DPI, only the policy whose rule matched has a say.
    public bool SecureDnsProcess { get; set; }

    // Secure DNS, machine side: a query nobody tracked asked — the Windows DNS client service
    // answering for everyone, or any process no filter caught — whose name one of this policy's
    // DOMAIN rules claims is resolved over HTTPS through this policy's outbound. Rules on IPs, ports,
    // protocol or "any" say nothing about a name and are not read for it. Always off on the
    // built-in Default policy, whose one rule matches everything.
    public bool SecureDnsSystem { get; set; }

    // When DNS over HTTPS fails for a query this policy took over, let the original plain query go
    // out instead of answering with a failure.
    public bool SecureDnsFallbackToPlain { get; set; }

    public override string ToString() => Name;
}
