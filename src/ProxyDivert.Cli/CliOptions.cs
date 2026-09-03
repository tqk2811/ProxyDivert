using System;
using System.Collections.Generic;
using System.Globalization;
using ProxyDivert.Core.Routing.Enums;
using TqkLibrary.WinDivert.Redirect.Enums;

namespace ProxyDivert.Cli;

// Command line of the test console. Hand-rolled because the whole surface is a dozen flags and a
// parser library would be the largest dependency in the project.
public sealed class CliOptions
{
    // Host an HTTP proxy on 127.0.0.1:<port> inside this process and route through it. 0 = off.
    // Traffic leaving that proxy comes from THIS process, so it is never redirected back into
    // itself — which is what makes it a usable end-to-end test of the proxy path.
    public int SelfHostPort { get; private set; }

    // Route through an existing proxy instead ("socks5://127.0.0.1:1080").
    public string? ProxyUrl { get; private set; }

    // Route through a WireGuard tunnel: path to the .conf, plus where wireproxy.exe is if it is
    // not next to this exe or on PATH.
    public string? VpnConfig { get; private set; }
    public string? WireProxyPath { get; private set; }

    // Launch this program suspended, attach, then resume it. The only way to be sure not one
    // connection escapes before the redirect is in place.
    public string? LaunchExe { get; private set; }
    public string? LaunchArgs { get; private set; }

    // Attach to processes that are already running: by pid, or by name/path rule.
    public List<uint> Pids { get; } = new List<uint>();
    public string? ProcessPattern { get; private set; }

    // Destination pattern that goes through the outbound. "*" (default) sends everything through
    // it; anything else leaves non-matching destinations direct, which is the interesting test.
    public string RulePattern { get; private set; } = "*";
    public HostMatcherType RuleMatcher { get; private set; } = HostMatcherType.Wildcard;

    public UdpMode UdpMode { get; private set; } = UdpMode.Direct;
    public bool BlockQuic { get; private set; } = true;
    public Ipv6Mode Ipv6 { get; private set; } = Ipv6Mode.Redirect;
    public Ipv6Support OutboundIpv6 { get; private set; } = Ipv6Support.Auto;

    // Stop after this many seconds. 0 = run until Ctrl+C.
    public int DurationSeconds { get; private set; }

    // Packet-level trace file. Off by default: with tracing on the log is thousands of lines a
    // second and the interesting connection lines are lost in it.
    public string? LogFile { get; private set; }
    public bool Verbose { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next(string name)
            {
                if (i + 1 >= args.Length) throw new FormatException($"{name} needs a value.");
                return args[++i];
            }

            switch (arg)
            {
                case "--selfhost": options.SelfHostPort = ParsePort(Next(arg)!); break;
                case "--proxy": options.ProxyUrl = Next(arg); break;
                case "--vpn": options.VpnConfig = Next(arg); break;
                case "--wireproxy": options.WireProxyPath = Next(arg); break;
                case "--launch": options.LaunchExe = Next(arg); break;
                case "--launch-args": options.LaunchArgs = Next(arg); break;
                case "--pid": options.Pids.Add(uint.Parse(Next(arg)!, CultureInfo.InvariantCulture)); break;
                case "--process": options.ProcessPattern = Next(arg); break;
                case "--rule": options.RulePattern = Next(arg)!; break;
                case "--matcher": options.RuleMatcher = Enum.Parse<HostMatcherType>(Next(arg)!, ignoreCase: true); break;
                case "--udp": options.UdpMode = Enum.Parse<UdpMode>(Next(arg)!, ignoreCase: true); break;
                case "--allow-quic": options.BlockQuic = false; break;
                case "--ipv6": options.Ipv6 = Enum.Parse<Ipv6Mode>(Next(arg)!, ignoreCase: true); break;
                case "--outbound-ipv6": options.OutboundIpv6 = Enum.Parse<Ipv6Support>(Next(arg)!, ignoreCase: true); break;
                case "--duration": options.DurationSeconds = int.Parse(Next(arg)!, CultureInfo.InvariantCulture); break;
                case "--log": options.LogFile = Next(arg); break;
                case "--verbose": options.Verbose = true; break;
                case "-h":
                case "--help": throw new HelpRequestedException();
                default: throw new FormatException($"Unknown argument '{arg}'.");
            }
        }

        int ways = (options.SelfHostPort != 0 ? 1 : 0) + (options.ProxyUrl != null ? 1 : 0) + (options.VpnConfig != null ? 1 : 0);
        if (ways == 0)
            throw new FormatException("Give --selfhost <port>, --proxy <url> or --vpn <config.conf>.");
        if (ways > 1)
            throw new FormatException("--selfhost, --proxy and --vpn are mutually exclusive.");
        if (options.LaunchExe == null && options.Pids.Count == 0 && options.ProcessPattern == null)
            throw new FormatException("Give --launch <exe>, --pid <id> or --process <name> — otherwise nothing is redirected.");

        return options;
    }

    private static int ParsePort(string value)
    {
        int port = int.Parse(value, CultureInfo.InvariantCulture);
        if (port is < 1 or > 65535) throw new FormatException($"Port out of range: {value}");
        return port;
    }

    public static string HelpText =>
        """
        ProxyDivert.Cli — redirect a process through a proxy and watch what happens.
        Must run as Administrator (WinDivert loads a kernel driver).

        Outbound (pick one):
          --selfhost <port>     host an HTTP proxy in this process and route through it
          --proxy <url>         use an existing proxy (http://, socks4://, socks5://)
          --vpn <config.conf>   route through a WireGuard tunnel (user space, via wireproxy)
          --wireproxy <exe>     where wireproxy.exe is, when not on PATH

        What to redirect (at least one):
          --launch <exe>        start it suspended, attach, then resume
          --launch-args <args>  arguments for --launch
          --pid <id>            attach to a running process id (repeatable)
          --process <name>      attach to every process matching this exe name

        Routing:
          --rule <pattern>      destinations that go through the outbound (default "*")
          --matcher <type>      Wildcard|DomainSuffix|Equals|Regex|IpCidr|Port (default Wildcard)
          --udp <mode>          Direct|ThroughOutbound|Block (default Direct)
          --allow-quic          do not block UDP/443
          --ipv6 <mode>         Redirect|Block|Ignore for the target's IPv6 (default Redirect)
          --outbound-ipv6 <s>   Auto|Enabled|Disabled — whether the outbound has an IPv6 route

        Run:
          --duration <seconds>  stop after N seconds (default: until Ctrl+C)
          --log <path>          write the packet-level trace to a file
          --verbose             print that trace to the console too

        Example — send this Chrome through a local proxy, leave the rest of the machine alone:
          ProxyDivert.Cli --selfhost 18080 ^
            --launch "C:\Program Files\Google\Chrome\Application\chrome.exe" ^
            --launch-args "--user-data-dir=C:\temp\cprof https://example.com"
        """;
}

public sealed class HelpRequestedException : Exception
{
}
