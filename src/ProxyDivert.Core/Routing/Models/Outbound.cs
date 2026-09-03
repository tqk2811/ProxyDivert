using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models;

// One way out of the machine. Direct and Block need nothing else; the proxy kinds carry a URL and
// optional credentials.
//
// The password is kept here in clear text ONLY in memory — ConfigStore encrypts it with DPAPI
// before it reaches disk.
public sealed class Outbound
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    public required OutboundKind Kind { get; set; }

    // "http://host:port", "socks5://host:port". Null for Direct and Block.
    public string? Url { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool IsEnabled { get; set; } = true;

    // True when this outbound can carry UDP (SOCKS5 UDP ASSOCIATE). Direct carries UDP too.
    public bool SupportsUdp => Kind is OutboundKind.Direct or OutboundKind.Socks5 or OutboundKind.Vpn;

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
