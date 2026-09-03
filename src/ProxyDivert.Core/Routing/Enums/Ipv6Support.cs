namespace ProxyDivert.Core.Routing.Enums;

// Whether an outbound can carry IPv6 to the destination. A proxy or VPN that has no IPv6 route of
// its own cannot be told apart from one that has — the protocol says nothing about it — so this is
// a user setting with a self-correcting default.
public enum Ipv6Support
{
    // Try IPv6, and remember the answer: the first destination that turns out to be unreachable
    // over IPv6 through this outbound switches it to "no IPv6" for the rest of the session, so
    // later connections go out over IPv4 without paying for the failed attempt again. Reset when
    // the outbound is edited.
    Auto = 0,

    // The outbound has an IPv6 route — send IPv6 destinations as IPv6.
    Enabled = 1,

    // The outbound has no IPv6 route. Named destinations are handed over as names so it resolves
    // them to IPv4 on its own side; an IPv6 literal with no name behind it has nothing to fall back
    // to, so the connection is refused and the application retries over IPv4 (Happy Eyeballs).
    Disabled = 2,
}
