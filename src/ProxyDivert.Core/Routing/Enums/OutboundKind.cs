namespace ProxyDivert.Core.Routing.Enums;

// What an outbound does with a connection routed to it.
public enum OutboundKind
{
    // Straight to the original destination (still through the relay, so it is counted and logged).
    Direct = 0,

    // Refuse the connection. Used to stop traffic leaking while a policy is being edited, and as
    // the safe answer for UDP the outbound cannot carry.
    Block = 1,

    HttpProxy = 2,
    Socks4 = 3,
    Socks5 = 4,

    // Phase 2 — a VPN tunnel exposed as an IProxySource.
    Vpn = 5,

    // One authenticated SSH session held open, each connection a direct-tcpip channel on it — what
    // `ssh -D` does, without a local SOCKS listener in between. TCP only: SSH has no channel for UDP.
    Ssh = 6,
}
