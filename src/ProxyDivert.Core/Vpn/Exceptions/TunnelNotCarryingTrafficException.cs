using System;
using System.Net;

namespace ProxyDivert.Core.Vpn.Exceptions;

/// <summary>
/// The tunnel is up as far as its driver is concerned, but nothing crosses it: the DNS server the
/// VPN itself handed out answered none of our queries.
/// </summary>
/// <remarks>
/// Worth its own type because of what it lets the caller skip. A name lookup that fails here used
/// to fall through to 1.1.1.1 and 8.8.8.8 — which travel the same dead tunnel — so every request
/// sat for thirty seconds before failing. The VPN's own resolver is the one address that is
/// certainly reachable from inside a working tunnel; its silence is an answer about the tunnel, not
/// about the name, and there is nothing left to ask.
/// </remarks>
public sealed class TunnelNotCarryingTrafficException : Exception
{
    public TunnelNotCarryingTrafficException(IPAddress dnsServer, TimeSpan waited)
        : base($"the VPN's own DNS server {dnsServer} did not answer two queries in {waited.TotalSeconds:0.#}s, "
             + "so the tunnel is up but no longer carrying traffic")
    {
        DnsServer = dnsServer;
    }

    /// <summary>The resolver whose silence was taken as the verdict.</summary>
    public IPAddress DnsServer { get; }
}
