namespace ProxyDivert.Core.Vpn.Enums;

// Which VPN a Vpn outbound speaks. Auto is the normal setting: the protocol is read off the URL or
// the configuration file, and this enum only exists so the user can say otherwise when the guess is
// wrong — or when the same file could be run two different ways.
public enum VpnProtocol
{
    // Work it out from the outbound's URL: a scheme names the protocol outright, a file path is
    // recognised by its extension and contents.
    Auto = 0,

    // A WireGuard .conf run by the external wireproxy.exe, which exposes the tunnel as a loopback
    // SOCKS5 listener. What a .conf gets by default, because it is what existing configurations
    // already use. TCP only.
    WireGuardWireProxy = 1,

    // The same .conf run by TqkLibrary.VpnClient inside this process: no external binary, and UDP
    // and IPv6 go through the tunnel too. Chosen explicitly, never guessed.
    WireGuard = 2,

    // A .ovpn profile, run in process.
    OpenVpn = 3,

    // The four below take a server and credentials rather than a provider's file, because no
    // standard client configuration file exists for them.
    Sstp = 4,
    L2tpIpsec = 5,
    Ikev2 = 6,
    SoftEther = 7,
}
