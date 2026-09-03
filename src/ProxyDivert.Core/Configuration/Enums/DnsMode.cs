namespace ProxyDivert.Core.Configuration.Enums;

// Where the target process's DNS answers come from.
public enum DnsMode
{
    // Let DNS go out as usual and only listen to the answers to learn IP -> domain.
    // Leaks the queried names to whoever runs the DNS server, exactly as before the tool.
    SystemSniff = 0,

    // Intercept UDP/53 and resolve over HTTPS instead. Keeps DNS working when the proxy cannot
    // carry UDP, and hides the queries from the local network.
    DnsOverHttps = 1,
}
