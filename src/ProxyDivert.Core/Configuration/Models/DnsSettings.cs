namespace ProxyDivert.Core.Configuration.Models;

public sealed class DnsSettings
{
    // The DNS over HTTPS server the policies that turn secure DNS on resolve through. An IP literal
    // avoids the bootstrap problem of resolving the resolver's own name; Cloudflare's certificate
    // carries 1.1.1.1 as an IP SAN. A host name works too: the lookup of that one name is always
    // let through untouched.
    public string DohEndpoint { get; set; } = "https://1.1.1.1/dns-query";
}
