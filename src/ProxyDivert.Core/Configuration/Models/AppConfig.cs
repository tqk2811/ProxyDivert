using System;
using System.Collections.Generic;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Configuration.Models;

// Everything the tool remembers between runs. Serialised to JSON next to the executable;
// passwords are encrypted before they get there (see ConfigStore).
public sealed class AppConfig
{
    // Bumped when a future change needs a migration step on load.
    public int Version { get; set; } = 1;

    public List<Outbound> Outbounds { get; set; } = new List<Outbound>();

    public List<RoutingPolicy> Policies { get; set; } = new List<RoutingPolicy>();

    public List<ProcessRule> ProcessRules { get; set; } = new List<ProcessRule>();

    public DnsSettings Dns { get; set; } = new DnsSettings();

    // Written next to the executable when set. Null = no packet-level trace file (the in-memory
    // log pane still works).
    public string? DiagnosticLogPath { get; set; }

    // Drop the target's IPv6 traffic. The redirector is IPv4-only, so leaving IPv6 alone would let
    // any AAAA-resolved connection bypass the proxy entirely.
    public bool BlockIpv6 { get; set; } = true;

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
