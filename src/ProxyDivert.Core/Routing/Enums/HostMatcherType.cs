namespace ProxyDivert.Core.Routing.Enums;

// How a routing rule compares a connection's destination against its pattern.
public enum HostMatcherType
{
    // "*.example.com", "cdn?.example.com" — the form most people reach for first.
    Wildcard = 0,

    // Exact host name, case-insensitive.
    Equals = 1,

    // Matches the host and every subdomain: "example.com" also matches "www.example.com".
    DomainSuffix = 2,

    StartsWith = 3,
    EndsWith = 4,
    Contains = 5,

    // .NET regular expression, case-insensitive. An invalid pattern never matches.
    Regex = 6,

    // "10.0.0.0/8", "1.2.3.4" — compared against the destination IP, not the name.
    IpCidr = 7,

    // Destination port. Pattern is a single port ("443") or a range ("8000-8100").
    Port = 8,
}
