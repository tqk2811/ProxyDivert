using System;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Enums;

namespace ProxyDivert.Core.Routing.Models;

// One way out of the machine. Direct and Block need nothing else; the proxy kinds carry a URL and
// optional credentials.
//
// The password and the pre-shared key are kept here in clear text ONLY in memory — ConfigStore
// encrypts both with DPAPI before they reach disk.
public sealed class Outbound
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    public required OutboundKind Kind { get; set; }

    // "http://host:port", "socks5://host:port". Null for Direct and Block.
    //
    // For Vpn it is either a configuration file the provider gave you (a .ovpn or a .conf), or the
    // VPN server itself for the protocols that have no such file ("sstp://vpn.example.com:443").
    // See VpnProfileReader, which is the only thing that interprets it.
    public string? Url { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    // The IPsec group pre-shared key, for the L2TP/IPsec and IKEv2 outbounds. It is a secret, so it
    // gets a box of its own rather than being tucked into the URL where it would be stored in the
    // clear and shown on screen.
    public string? PreSharedKey { get; set; }

    // Which VPN this outbound speaks. Auto reads it off the URL, which is right nearly always; the
    // one thing it cannot guess is whether a WireGuard .conf should be run by wireproxy (what it
    // has always done, and still the default) or in this process.
    public VpnProtocol VpnProtocol { get; set; } = VpnProtocol.Auto;

    public bool IsEnabled { get; set; } = true;

    // Whether this way out can reach IPv6 destinations. See Ipv6Support — Auto learns it from the
    // first failure rather than asking the user to know.
    public Ipv6Support Ipv6Support { get; set; } = Ipv6Support.Auto;

    // True when this outbound can carry UDP (SOCKS5 UDP ASSOCIATE). Direct carries UDP too.
    //
    // A VPN depends on which engine runs it. wireproxy's SOCKS5 implementation is TCP-only, so a
    // .conf running on it downgrades "UDP through the outbound" to Block rather than leaking the
    // datagrams; a tunnel run in this process owns a whole userspace IP stack and carries UDP
    // itself. The question is answered from the URL alone, never by reading the file — this is on
    // the routing path, once per connection.
    [JsonIgnore]
    public bool SupportsUdp => Kind switch
    {
        OutboundKind.Direct or OutboundKind.Socks5 => true,
        OutboundKind.Vpn => !VpnProfileReader.RunsOnWireProxy(VpnProtocol, Url),
        _ => false,
    };

    // SOCKS4 has no IPv6 in the protocol at all — no address type for it — so no setting can make
    // it carry IPv6. Direct is the machine's own stack: if the machine has IPv6, so does Direct.
    [JsonIgnore]
    public bool CanEverCarryIpv6 => Kind != OutboundKind.Socks4;

    // Direct and Block are the two the application creates for itself. They exist so a policy has
    // something to point at, they carry no settings anyone could sensibly change, and a rule that
    // references one by id would break if it were renamed or given a URL — so the list shows them
    // and lets nothing be typed into them.
    [JsonIgnore]
    public bool IsBuiltIn => Id == DirectId || Id == BlockId;

    public static Outbound CreateDirect() => new Outbound
    {
        Id = DirectId,
        Name = "Direct",
        Kind = OutboundKind.Direct,
    };

    public static Outbound CreateBlock() => new Outbound
    {
        Id = BlockId,
        Name = "Block",
        Kind = OutboundKind.Block,
    };

    // Fixed ids so a policy can reference the two built-ins without them having to exist in the
    // user's outbound list, and so a config file stays readable.
    public static readonly Guid DirectId = new Guid("00000000-0000-0000-0000-000000000001");
    public static readonly Guid BlockId = new Guid("00000000-0000-0000-0000-000000000002");

    public override string ToString() => $"{Name} ({Kind})";
}
