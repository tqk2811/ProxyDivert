using System;
using System.Net;

namespace ProxyDivert.Core.Routing.Models;

// What is known about a connection at the moment the routing decision is made.
//
// Host is null when the connection revealed no name (no SNI, no Host header, nothing in the
// reverse-DNS table) — rules that match on names simply don't apply then, and the connection
// falls through to an IP/port rule or to the policy default.
public sealed class RouteTarget
{
    public uint ProcessId { get; }
    public IPAddress Address { get; }
    public int Port { get; }
    public string? Host { get; }
    public bool IsUdp { get; }

    public RouteTarget(uint processId, IPAddress address, int port, string? host, bool isUdp = false)
    {
        ProcessId = processId;
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Port = port;
        Host = string.IsNullOrWhiteSpace(host) ? null : host!.Trim();
        IsUdp = isUdp;
    }

    public override string ToString()
        => Host != null ? $"{Host} [{Address}:{Port}]" : $"{Address}:{Port}";
}
