using System;
using System.Net;
using TqkLibrary.WinDivert.Redirect.Models;

namespace ProxyDivert.Core.Engine.Models;

// One redirected connection as the UI sees it. Byte counters are read live from the underlying
// ConnectionStatistics, so a table bound to this object shows traffic as it happens without the
// engine pushing updates per packet.
public sealed class ConnectionInfo
{
    public Guid Id { get; } = Guid.NewGuid();

    public uint ProcessId { get; }
    public string ProcessName { get; }

    // SNI / Host / reverse-DNS name, or null when the connection revealed none.
    public string? Host { get; set; }

    public IPEndPoint Destination { get; }
    public bool IsUdp { get; }

    // Name of the outbound the router picked, and why.
    public string OutboundName { get; set; } = string.Empty;
    public string RouteReason { get; set; } = string.Empty;

    public ConnectionStatistics Statistics { get; }

    public long BytesUp => Statistics.BytesFromProcess;
    public long BytesDown => Statistics.BytesToProcess;
    public DateTime StartedUtc => Statistics.StartedUtc;
    public DateTime? EndedUtc => Statistics.EndedUtc;
    public bool IsActive => Statistics.EndedUtc == null;

    // Set when the connection failed to establish through its outbound.
    public string? Error { get; set; }

    public ConnectionInfo(uint processId, string processName, IPEndPoint destination, ConnectionStatistics statistics, bool isUdp = false)
    {
        ProcessId = processId;
        ProcessName = processName;
        Destination = destination;
        Statistics = statistics;
        IsUdp = isUdp;
    }

    public override string ToString()
        => $"[{ProcessId}] {ProcessName} -> {Host ?? Destination.ToString()} via {OutboundName}";
}
