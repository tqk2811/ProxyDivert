namespace ProxyDivert.Core.Outbounds.Enums;

/// <summary>
/// What the one address box on an outbound turns out to hold. Four different things go in it, and
/// telling them apart is a decision on its own — separate from what is then done about it.
/// </summary>
public enum OutboundAddressKind
{
    /// <summary>A proxy to send connections to: <c>socks5://127.0.0.1:1080</c>.</summary>
    ProxyEndpoint,

    /// <summary>
    /// A VPN server to dial, for the protocols that have no standard client file:
    /// <c>sstp://vpn.example.com:443</c>, <c>softether://vpn.example.com:443/HUB</c>.
    /// </summary>
    VpnEndpoint,

    /// <summary>A configuration file the VPN provider gave you: a <c>.ovpn</c> or a <c>.conf</c>.</summary>
    VpnConfigFile,

    /// <summary>
    /// A small <c>.vpn</c> ini holding what a <see cref="VpnEndpoint"/> would have said, for anyone
    /// who would rather keep it in a file than in the row.
    /// </summary>
    VpnIniFile,
}
