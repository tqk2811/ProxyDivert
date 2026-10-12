namespace ProxyDivert.Api.Models;

public sealed class SetOutboundRequest
{
    public required Guid OutboundId { get; init; }
}
