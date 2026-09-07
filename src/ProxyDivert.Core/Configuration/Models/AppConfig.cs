using System;
using System.Collections.Generic;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect.Enums;

namespace ProxyDivert.Core.Configuration.Models;

// Everything the tool remembers between runs. Serialised to JSON next to the executable;
// passwords are encrypted before they get there (see ConfigStore).
public sealed class AppConfig
{
    public List<Outbound> Outbounds { get; set; } = new List<Outbound>();

    public List<RoutingPolicy> Policies { get; set; } = new List<RoutingPolicy>();

    public List<ProcessRule> ProcessRules { get; set; } = new List<ProcessRule>();

    public DnsSettings Dns { get; set; } = new DnsSettings();

    // Written next to the executable when set. Null = no packet-level trace file (the in-memory
    // log pane still works).
    public string? DiagnosticLogPath { get; set; }

    // Keep a trace file without having to name one: each run writes to its own timestamped file
    // under Logs\ next to the executable. A fresh file per run is the point — a bug reported an
    // hour ago is still on disk instead of having been truncated by the next start — and it takes
    // precedence over DiagnosticLogPath, which stays for pinning the trace to a fixed place.
    public bool AutoSaveLog { get; set; }

    // What happens to the target's IPv6 traffic: Redirect (default) sends it through the relay and
    // the routing rules exactly like IPv4; Block drops it so the application falls back to IPv4;
    // Ignore lets it leave untouched (it then bypasses the proxy — diagnostics only).
    public Ipv6Mode Ipv6 { get; set; } = Ipv6Mode.Redirect;

    // Path to wireproxy.exe, which runs the WireGuard tunnel of a VPN outbound in user space.
    // Null = look next to this executable and then on PATH. One setting for the whole machine:
    // it is the same binary whichever tunnel it runs.
    public string? WireProxyPath { get; set; }

    // How a process is found in the first place: from a process event, or from the socket it
    // opens. See ProcessDetectionMode — the second one judges a pid at the moment it connects,
    // which is later than a process event but tells us about a process that connects immediately.
    public ProcessDetectionMode ProcessDetection { get; set; } = ProcessDetectionMode.ProcessEvents;

    // Where the process table hears about processes starting and stopping. ETW is the kernel's own
    // provider and the shortest path; WMI is the same events after a service has repackaged them,
    // kept for a machine where a trace session cannot be created. A source that will not start
    // falls back to the other one on its own, so this is a preference rather than a requirement.
    public ProcessEventSourceKind ProcessEventSource { get; set; } = ProcessEventSourceKind.Etw;

    // UI preferences kept with the rest so one file is the whole state.
    public string? Language { get; set; }
    public string? Theme { get; set; }
    public bool StartWithWindows { get; set; }

    // A fresh install still needs something that works: the Direct outbound, and one policy that
    // has no rules yet — so nothing is claimed and nothing is redirected until the user says so.
    public static AppConfig CreateDefault()
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            OutboundId = Outbound.DirectId,
        };
        return new AppConfig
        {
            Outbounds = { Outbound.CreateDirect(), Outbound.CreateBlock() },
            Policies = { policy },
        };
    }
}
