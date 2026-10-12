using ProxyDivert.Core.Vpn.Enums;

namespace ProxyDivert.Api.Models;

public sealed class ApiVpnStatus
{
    public required Guid OutboundId { get; init; }

    public required string OutboundName { get; init; }

    public required VpnConnectionState State { get; init; }

    public string? Error { get; init; }
}
