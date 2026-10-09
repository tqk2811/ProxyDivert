using System.Collections.Generic;

namespace ProxyDivert.ViewModels.Models;

/// <summary>
/// One suggestion in the DoH endpoint box: who runs it, and the URL that goes into the box.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> returns the URL on purpose: an editable ComboBox writes the picked item's
/// text into the box, and that text has to be the endpoint itself, not the provider's name.
/// </remarks>
public sealed record DohEndpointPreset(string Provider, string Url)
{
    // Suggestions for an endpoint box, which stays free text. IP-literal URLs first: they need no
    // lookup of their own, and these providers' certificates name those IPs.
    public static IReadOnlyList<DohEndpointPreset> All { get; } = new[]
    {
        new DohEndpointPreset("Cloudflare", "https://1.1.1.1/dns-query"),
        new DohEndpointPreset("Cloudflare", "https://1.0.0.1/dns-query"),
        new DohEndpointPreset("Google", "https://8.8.8.8/dns-query"),
        new DohEndpointPreset("Google", "https://8.8.4.4/dns-query"),
        new DohEndpointPreset("Google", "https://dns.google/dns-query"),
        new DohEndpointPreset("Quad9", "https://9.9.9.9/dns-query"),
        new DohEndpointPreset("Quad9", "https://dns.quad9.net/dns-query"),
        new DohEndpointPreset("AdGuard", "https://dns.adguard-dns.com/dns-query"),
    };

    public override string ToString() => Url;
}
