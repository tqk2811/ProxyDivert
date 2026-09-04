using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyDivert.Core.Vpn;

/// <summary>
/// A proxy source whose tunnel can be brought up ahead of the first request and then watched, so
/// the engine can hold it open instead of dialling one per connection.
/// </summary>
/// <remarks>
/// Two very different things implement this. wireproxy is a subprocess that either runs or does
/// not; a TqkLibrary.VpnClient tunnel is an in-process driver that reconnects on its own. The
/// difference is exactly what the two members below draw the line between: <see cref="IsRunning"/>
/// is "carrying traffic at this instant", which the UI reads, while
/// <see cref="WaitUntilDownAsync"/> completes only when this instance is finished for good and the
/// supervisor should build a new one. A driver's internal reconnect is therefore not reported as a
/// failure — it would be fighting the driver to tear the tunnel down while it is already fixing
/// itself.
/// </remarks>
public interface IKeptTunnel
{
    /// <summary>True while the tunnel is up and usable right now.</summary>
    bool IsRunning { get; }

    /// <summary>Where the tunnel comes out, for the log line. Never parsed.</summary>
    string Endpoint { get; }

    /// <summary>
    /// Brings the tunnel up, or returns immediately when it is already up. Throws when the
    /// configuration cannot produce a working tunnel at all.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes when the tunnel is beyond recovery by itself, with a human-readable reason. It is
    /// the supervisor's cue to build a replacement — not a report of a transient drop.
    /// </summary>
    Task<string> WaitUntilDownAsync(CancellationToken cancellationToken = default);
}
