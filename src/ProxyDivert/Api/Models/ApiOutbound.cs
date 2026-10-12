using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Api.Models;

/// <summary>
/// An outbound as the API shows it: enough to pick one for a policy, and nothing secret —
/// no password, key or pre-shared key, and no URL, which may carry credentials of its own.
/// </summary>
public sealed class ApiOutbound
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required OutboundKind Kind { get; init; }

    public required bool IsEnabled { get; init; }

    public required bool IsBuiltIn { get; init; }

    public static ApiOutbound From(Outbound outbound) => new()
    {
        Id = outbound.Id,
        Name = outbound.Name,
        Kind = outbound.Kind,
        IsEnabled = outbound.IsEnabled,
        IsBuiltIn = outbound.IsBuiltIn,
    };
}
