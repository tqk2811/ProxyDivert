using System;
using System.Collections.Concurrent;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Outbounds;

// Remembers which outbounds have turned out to be unable to reach IPv6.
//
// Nothing in the HTTP or SOCKS5 protocol says whether the far end has an IPv6 route: a proxy on a
// v4-only host accepts an IPv6 CONNECT and then fails it, exactly like a destination that happens
// to be down. So the honest default (Ipv6Support.Auto) is to try once and believe the answer —
// after the first failure this outbound is treated as IPv4-only for the rest of the session, which
// turns a repeated timeout into a fast, deliberate fall back to IPv4.
//
// The judgement is deliberately one-way: a success does NOT clear a previous failure, because a
// single destination happening to work says less than one failing. Editing the outbound (or
// stopping the engine) clears it.
public sealed class OutboundIpv6Capability
{
    // Only outbounds KNOWN to have failed appear here; absence means "not learned yet".
    private readonly ConcurrentDictionary<Guid, bool> _failed = new ConcurrentDictionary<Guid, bool>();

    /// <summary>
    /// Whether an IPv6 destination may be handed to this outbound as IPv6.
    /// </summary>
    public bool AllowsIpv6(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        if (!outbound.CanEverCarryIpv6) return false;

        return outbound.Ipv6Support switch
        {
            Ipv6Support.Enabled => true,
            Ipv6Support.Disabled => false,
            _ => !_failed.ContainsKey(outbound.Id),
        };
    }

    /// <summary>
    /// Records that an IPv6 destination could not be reached through this outbound. Only meaningful
    /// for Auto — a user who said Enabled is not overruled by one failed connection.
    /// Returns true when this is the first failure, i.e. when the state actually changed.
    /// </summary>
    public bool RecordIpv6Failure(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        if (outbound.Ipv6Support != Ipv6Support.Auto) return false;
        return _failed.TryAdd(outbound.Id, true);
    }

    /// <summary>Forget what was learned about one outbound — call when the user edits it.</summary>
    public void Reset(Guid outboundId) => _failed.TryRemove(outboundId, out _);

    public void ResetAll() => _failed.Clear();

    /// <summary>True when this outbound was learned (not configured) to be IPv4-only.</summary>
    public bool HasLearnedFailure(Guid outboundId) => _failed.ContainsKey(outboundId);
}
