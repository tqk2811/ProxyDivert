using System;
using ProxyDivert.Core.Vpn.Enums;

namespace ProxyDivert.Core.Vpn.Models;

/// <summary>
/// A snapshot of one VPN outbound's tunnel, as the keeper last saw it.
/// </summary>
/// <remarks>
/// Immutable on purpose: it crosses from the keeper's supervision thread to the window, and a
/// value that cannot change underneath the UI is one the UI never has to lock around.
/// </remarks>
public sealed class VpnStatus
{
    public VpnStatus(
        Guid outboundId, string outboundName, VpnConnectionState state,
        string? error = null, int retryCount = 0)
    {
        OutboundId = outboundId;
        OutboundName = outboundName;
        State = state;
        Error = error;
        RetryCount = retryCount;
        ChangedUtc = DateTime.UtcNow;
    }

    public Guid OutboundId { get; }

    public string OutboundName { get; }

    public VpnConnectionState State { get; }

    /// <summary>Why it is not up, when it is not. Null while things are working.</summary>
    public string? Error { get; }

    /// <summary>
    /// Failed attempts since the tunnel was last properly up. It is what makes a tunnel that keeps
    /// dying distinguishable from one that hiccuped once.
    /// </summary>
    public int RetryCount { get; }

    public DateTime ChangedUtc { get; }

    public override string ToString()
        => Error is null ? $"{OutboundName}: {State}" : $"{OutboundName}: {State} ({Error})";
}
