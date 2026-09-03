using System;
using ProxyDivert.Core.Configuration.Enums;

namespace ProxyDivert.Core.Configuration.Models;

public sealed class DnsSettings
{
    public DnsMode Mode { get; set; } = DnsMode.SystemSniff;

    // Used when Mode is DnsOverHttps. An IP literal avoids the bootstrap problem of resolving the
    // resolver's own name; Cloudflare's certificate carries 1.1.1.1 as an IP SAN.
    public string DohEndpoint { get; set; } = "https://1.1.1.1/dns-query";
}
