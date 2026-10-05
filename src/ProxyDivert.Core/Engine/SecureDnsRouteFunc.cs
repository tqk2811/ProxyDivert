using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The routing table's verdict for (pid, name, isIpv6): the decision, or null with the reason the
/// query is left alone.
/// </summary>
internal delegate DnsRouteDecision? SecureDnsRouteFunc(uint? processId, string queryName, bool isIpv6, out DnsPassReason reason);
