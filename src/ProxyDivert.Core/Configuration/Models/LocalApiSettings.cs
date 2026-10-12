namespace ProxyDivert.Core.Configuration.Models;

// The HTTP API another program on this machine (or the LAN) drives the tool through. Off until the
// user switches it on: the process runs elevated, so whoever can reach the port can change what
// every filtered program's traffic does.
public sealed class LocalApiSettings
{
    public const int DefaultPort = 5085;

    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    // False = 127.0.0.1 and ::1 only. True = every interface, for a tool on another machine.
    public bool ListenOnLan { get; set; }

    // When set, every request must carry "Authorization: Bearer <Token>".
    public bool RequireToken { get; set; }

    public string? Token { get; set; }
}
