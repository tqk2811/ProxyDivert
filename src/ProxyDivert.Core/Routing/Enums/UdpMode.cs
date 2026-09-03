namespace ProxyDivert.Core.Routing.Enums;

// What a policy does with the target's UDP (other than DNS, which has its own setting).
public enum UdpMode
{
    // Send it straight out. Fast, but the destination sees the real client IP.
    Direct = 0,

    // Tunnel through the policy's outbound. Only SOCKS5 can carry UDP; with any other outbound
    // this behaves as Block rather than leaking.
    ThroughOutbound = 1,

    // Drop it. The safe default for a proxy that cannot carry UDP.
    Block = 2,
}
