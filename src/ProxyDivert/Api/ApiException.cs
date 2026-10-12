namespace ProxyDivert.Api;

/// <summary>
/// A request the API refuses, carried up to the error middleware as an HTTP status and a stable
/// machine-readable code.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message, object? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }

    public int StatusCode { get; }

    public string Code { get; }

    public object? Details { get; }

    public static ApiException NotFound(string code, string message) => new(404, code, message);

    public static ApiException BadRequest(string code, string message, object? details = null)
        => new(400, code, message, details);

    public static ApiException Conflict(string code, string message) => new(409, code, message);
}
