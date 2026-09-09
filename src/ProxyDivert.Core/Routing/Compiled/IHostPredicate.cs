using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing.Compiled;

/// <summary>
/// One rule pattern, already parsed: does this connection's destination fit it?
/// </summary>
/// <remarks>
/// A pattern is text the user typed, and turning text into a decision is real work — parsing an
/// address and a prefix length, compiling a regular expression, splitting a port range. Doing that
/// inside the match meant doing it again for every rule of every connection, for the whole life of
/// a configuration that never changed in between.
///
/// A predicate is built once, when the configuration is compiled, and is immutable afterwards. That
/// also moves the moment a bad pattern is noticed: from "silently matches nothing, forever" to
/// "reported once, when it is compiled".
/// </remarks>
public interface IHostPredicate
{
    bool IsMatch(RouteTarget target);
}
