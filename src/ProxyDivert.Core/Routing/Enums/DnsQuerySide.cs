namespace ProxyDivert.Core.Routing.Enums;

/// <summary>Which half of the routing table took a DNS query over.</summary>
public enum DnsQuerySide
{
    /// <summary>A process under redirection, answered by its own policies.</summary>
    Process = 0,

    /// <summary>Anyone else (the Windows DNS client service, untracked processes), by the machine-side policies.</summary>
    System,
}
