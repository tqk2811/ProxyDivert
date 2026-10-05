namespace ProxyDivert.Core.Routing.Enums;

/// <summary>Why the routing table left a DNS query alone (it goes out as plain DNS).</summary>
public enum DnsPassReason
{
    /// <summary>The query is not being left alone: it was taken over.</summary>
    None = 0,

    /// <summary>The query has no name to judge.</summary>
    InvalidName,

    /// <summary>The name is the DoH server's or an outbound's own server: resolving it over DoH would loop.</summary>
    PassThroughHost,

    /// <summary>A tracked process whose claiming policy did not turn process-side secure DNS on.</summary>
    ProcessPolicyDeclined,

    /// <summary>A tracked process whose claiming policy sends the name to a Block outbound.</summary>
    ProcessBlocked,

    /// <summary>Not a tracked process, and no policy turned machine-side secure DNS on.</summary>
    NoSystemPolicy,

    /// <summary>Not a tracked process; machine-side policies exist but none has a rule claiming the name.</summary>
    NoMatchingPolicy,

    /// <summary>The machine-side policy claiming the name sends it to a Block outbound.</summary>
    SystemBlocked,

    /// <summary>The routing table threw while deciding.</summary>
    RoutingError,

    /// <summary>The engine is not running (no routing table).</summary>
    EngineStopped,
}
