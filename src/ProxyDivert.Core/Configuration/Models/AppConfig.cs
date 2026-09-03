using System;
using System.Collections.Generic;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect.Enums;

namespace ProxyDivert.Core.Configuration.Models;

// Everything the tool remembers between runs. Serialised to JSON next to the executable;
// passwords are encrypted before they get there (see ConfigStore).
public sealed class AppConfig
{
    // Bumped when a change needs a migration step on load (see ConfigStore.Migrate).
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public List<Outbound> Outbounds { get; set; } = new List<Outbound>();

    public List<RoutingPolicy> Policies { get; set; } = new List<RoutingPolicy>();

    public List<ProcessRule> ProcessRules { get; set; } = new List<ProcessRule>();

    public DnsSettings Dns { get; set; } = new DnsSettings();

    // Written next to the executable when set. Null = no packet-level trace file (the in-memory
    // log pane still works).
    public string? DiagnosticLogPath { get; set; }

    // What happens to the target's IPv6 traffic: Redirect (default) sends it through the relay and
    // the routing rules exactly like IPv4; Block drops it so the application falls back to IPv4;
    // Ignore lets it leave untouched (it then bypasses the proxy — diagnostics only).
    public Ipv6Mode Ipv6 { get; set; } = Ipv6Mode.Redirect;

    // Config v1 only. Back then the redirector was IPv4-only and this switch decided whether the
    // target's IPv6 was dropped. ConfigStore maps it onto Ipv6 on load and then clears it, so it is
    // never written back (null properties are omitted).
    public bool? BlockIpv6 { get; set; }

    // Path to wireproxy.exe, which runs the WireGuard tunnel of a VPN outbound in user space.
    // Null = look next to this executable and then on PATH. One setting for the whole machine:
    // it is the same binary whichever tunnel it runs.
    public string? WireProxyPath { get; set; }

    // UI preferences kept with the rest so one file is the whole state.
    public string? Language { get; set; }
    public string? Theme { get; set; }
    public bool StartWithWindows { get; set; }

    // A fresh install still needs something that works: one Direct outbound and a policy that
    // sends everything through it.
    public static AppConfig CreateDefault()
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            DefaultOutboundId = Outbound.DirectId,
        };
        return new AppConfig
        {
            Outbounds = { Outbound.CreateDirect(), Outbound.CreateBlock() },
            Policies = { policy },
        };
    }
}
