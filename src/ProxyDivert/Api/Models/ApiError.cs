namespace ProxyDivert.Api.Models;

/// <summary>The body of every non-2xx answer.</summary>
public sealed class ApiError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public object? Details { get; init; }
}
