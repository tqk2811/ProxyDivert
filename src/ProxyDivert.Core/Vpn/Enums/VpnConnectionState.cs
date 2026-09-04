namespace ProxyDivert.Core.Vpn.Enums;

/// <summary>
/// Where a kept VPN tunnel is in its life. There is no "Disconnected": a tunnel the keeper holds
/// is always either up or on its way back up, and one it does not hold is simply absent from the
/// status list.
/// </summary>
public enum VpnConnectionState
{
    /// <summary>Bringing wireproxy up. The first connection through this outbound would wait.</summary>
    Connecting,

    /// <summary>
    /// Up: the subprocess is running and its SOCKS5 listener accepts. A connection through this
    /// outbound starts tunnelling straight away.
    /// </summary>
    Connected,

    /// <summary>
    /// It failed or died, and the keeper is waiting out its backoff before trying again. The
    /// status carries the reason.
    /// </summary>
    Reconnecting,

    /// <summary>Not kept any more — disabled, deleted, or the engine stopped.</summary>
    Stopped,
}
