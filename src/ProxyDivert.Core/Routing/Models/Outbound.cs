using System;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Outbounds.Enums;
using ProxyDivert.Core.Outbounds.Models;
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

    public required OutboundKind Kind
    {
        get => _kind;
        set { _kind = value; _address = null; }
    }

    // "http://host:port", "socks5://host:port". Null for Direct and Block.
    //
    // For Vpn it is either a configuration file the provider gave you (a .ovpn or a .conf), or the
    // VPN server itself for the protocols that have no such file ("sstp://vpn.example.com:443").
    // What it turns out to be is Address; what to do about that is VpnProfileReader's business.
    public string? Url
    {
        get => _url;
        set { _url = value; _address = null; }
    }

    /// <summary>
    /// What <see cref="Url"/> means, read once, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Cached because the routing path asks: whether a VPN carries UDP comes down to what is in
    /// that box, and it is asked once per connection. The three properties the answer depends on
    /// throw the cache away when they are set, so a row edited in the grid is re-read rather than
    /// answering as it used to.
    ///
    /// The cache is one reference holding the answer rather than a value plus a "have we done it
    /// yet" flag: a connection thread reading this while another one fills it in must see both or
    /// neither, and two fields can be published in either order.
    /// </remarks>
    [JsonIgnore]
    public OutboundAddress? Address
    {
        get
        {
            Parsed? parsed = _address;
            if (parsed is null)
            {
                OutboundAddress.TryParse(_kind, _url, _vpnProtocol, out OutboundAddress? read, out _);
                _address = parsed = new Parsed(read);
            }
            return parsed.Value;
        }
    }

    /// <summary>Why <see cref="Url"/> cannot be read, or null when it can — or wants no address.</summary>
    [JsonIgnore]
    public string? AddressProblem
    {
        get
        {
            OutboundAddress.TryParse(_kind, _url, _vpnProtocol, out _, out string? error);
            return error;
        }
    }

    private OutboundKind _kind;
    private string? _url;
    private volatile Parsed? _address;

    private sealed class Parsed
    {
        public Parsed(OutboundAddress? value) => Value = value;

        public OutboundAddress? Value { get; }
    }

    public string? Username { get; set; }

    public string? Password { get; set; }

    // The private key file an SSH outbound logs in with — OpenSSH, PuTTY or PEM, as SSH.NET reads
    // them. A path rather than the key itself, so the key stays where the user keeps it and out of
    // the configuration file. When it is set, Password is the key's passphrase (and is still offered
    // as a login password, for a server that wants either); without it Password is the password.
    public string? PrivateKeyPath { get; set; }

    // The IPsec group pre-shared key, for the L2TP/IPsec and IKEv2 outbounds. It gets a box of its
    // own rather than being tucked into the URL, so it can be edited on its own and hidden on
    // screen — the same treatment as the password beside it.
    public string? PreSharedKey { get; set; }

    // Which VPN this outbound speaks. Auto reads it off the URL, which is right nearly always; the
    // one thing it cannot guess is whether a WireGuard .conf should be run by wireproxy (what it
    // has always done, and still the default) or in this process.
    public VpnProtocol VpnProtocol
    {
        get => _vpnProtocol;
        set { _vpnProtocol = value; _address = null; }
    }

    private VpnProtocol _vpnProtocol = VpnProtocol.Auto;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether this VPN's tunnel is to be held up. Meaningless for every other kind — only a VPN
    /// has a tunnel to keep.
    /// </summary>
    /// <remarks>
    /// It is deliberately not tied to the engine: the tunnel is a connection to a VPN provider, not
    /// part of a redirection run, so turning WinDivert off leaves it up and turning WinDivert on
    /// sets it for whatever the rules actually route through a VPN. Stored rather than kept in
    /// memory so the tunnels that were up come back the next time the window opens.
    /// </remarks>
    public bool KeepConnected { get; set; }

    // Whether this way out can reach IPv6 destinations. See Ipv6Support — Auto learns it from the
    // first failure rather than asking the user to know.
    public Ipv6Support Ipv6Support { get; set; } = Ipv6Support.Auto;

    // True when this outbound can carry UDP (SOCKS5 UDP ASSOCIATE). Direct carries UDP too. SSH never
    // does — its only forwarding channel is a TCP stream — so it falls under the last arm with the
    // HTTP and SOCKS4 proxies.
    //
    // A VPN depends on which engine runs it. wireproxy's SOCKS5 implementation is TCP-only, so a
    // .conf running on it downgrades "UDP through the outbound" to Block rather than leaking the
    // datagrams; a tunnel run in this process owns a whole userspace IP stack and carries UDP
    // itself. The question is answered from the URL alone, never by reading the file — this is on
    // the routing path, once per connection, which is also why it asks the already-read Address
    // rather than the string.
    [JsonIgnore]
    public bool SupportsUdp => Kind switch
    {
        OutboundKind.Direct or OutboundKind.Socks5 => true,
        OutboundKind.Vpn => !VpnProfileReader.RunsOnWireProxy(_vpnProtocol, Address),
        _ => false,
    };

    // SOCKS4 has no IPv6 in the protocol at all — no address type for it — so no setting can make
    // it carry IPv6. Direct is the machine's own stack: if the machine has IPv6, so does Direct.
    [JsonIgnore]
    public bool CanEverCarryIpv6 => Kind != OutboundKind.Socks4;

    // The two kinds that are not a way out but an answer, named so that the code asking reads as
    // what it means rather than as a comparison against an enum. Both are asked all over the engine
    // and the resolver, on the packet path and the connection path, and "Kind == OutboundKind.Direct"
    // spelled out at each of them says how the check is made instead of what it decides.
    [JsonIgnore]
    public bool IsDirect => Kind == OutboundKind.Direct;

    [JsonIgnore]
    public bool IsBlocked => Kind == OutboundKind.Block;

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
