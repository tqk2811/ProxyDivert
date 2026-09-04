using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Cli;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy;
using TqkLibrary.Proxy.Handlers;
using TqkLibrary.Proxy.ProxySources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.DependencyInjection;
using ProxyDivert.Core.Logging;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using TqkLibrary.WinDivert.ProcessControl.Models;

// Console harness for the redirect engine: no window, no config file, everything from arguments.
// It exists so the engine can be exercised end to end — including against a process that is
// already running — without going through the UI.

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliOptions.HelpText);
    return 2;
}

if (!IsElevated())
{
    Console.Error.WriteLine("Must run as Administrator: WinDivert loads a kernel driver.");
    return 1;
}

using var exitCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exitCts.Cancel(); };

// ---- outbound -------------------------------------------------------------------------------

ProxyServer? selfHosted = null;
Outbound outbound;

if (options.SelfHostPort != 0)
{
    // A plain HTTP proxy backed by LocalProxySource: it connects to the real destination on our
    // behalf. Because it runs in THIS process, its own traffic is not redirected, so the path
    // Chrome -> relay -> proxy -> internet is a genuine round trip rather than a loop.
    var backend = new LocalProxySource();
    selfHosted = new ProxyServer(new IPEndPoint(IPAddress.Loopback, options.SelfHostPort), backend)
    {
        ProxyServerHandler = new BaseProxyServerHandler(backend),
    };
    selfHosted.StartListen();
    IPEndPoint? listening = selfHosted.IPEndPoint;
    if (listening is null)
    {
        Console.Error.WriteLine($"Could not bind the self-hosted proxy on port {options.SelfHostPort}.");
        return 1;
    }

    outbound = new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "selfhost",
        Kind = OutboundKind.HttpProxy,
        Url = $"http://{listening}",
        Ipv6Support = options.OutboundIpv6,
    };
    Console.WriteLine($"Self-hosted HTTP proxy: http://{listening}  (backend = direct)");
}
else if (options.VpnConfig != null)
{
    outbound = new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "vpn",
        Kind = OutboundKind.Vpn,
        Url = options.VpnConfig,
        Username = options.VpnUser,
        Password = options.VpnPass,
        PreSharedKey = options.VpnPsk,
        VpnProtocol = options.VpnProtocol,
        Ipv6Support = options.OutboundIpv6,
    };
    Console.WriteLine($"VPN tunnel: {options.VpnConfig}");
}
else
{
    string url = options.ProxyUrl!;
    outbound = new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "proxy",
        Kind = KindFromUrl(url),
        Url = url,
        Ipv6Support = options.OutboundIpv6,
    };
    Console.WriteLine($"Upstream proxy: {url}");
}

// ---- configuration --------------------------------------------------------------------------

var policy = new RoutingPolicy
{
    Id = Guid.NewGuid(),
    Name = "cli",
    DefaultOutboundId = Outbound.DirectId,
    UdpMode = options.UdpMode,
    BlockQuic = options.BlockQuic,
};
policy.Rules.Add(new RoutingRule
{
    Id = Guid.NewGuid(),
    Matcher = options.RuleMatcher,
    Pattern = options.RulePattern,
    OutboundId = outbound.Id,
    Order = 0,
});

var config = new AppConfig
{
    Outbounds = { Outbound.CreateDirect(), Outbound.CreateBlock(), outbound },
    Policies = { policy },
    Ipv6 = options.Ipv6,
    WireProxyPath = options.WireProxyPath,
    DiagnosticLogPath = options.LogFile,
};

if (options.ProcessPattern != null)
{
    config.ProcessRules.Add(new ProcessRule
    {
        Id = Guid.NewGuid(),
        Matcher = ProcessMatcherType.ExeName,
        Pattern = options.ProcessPattern,
        PolicyId = policy.Id,
        IncludeChildren = true,
    });
}

Console.WriteLine($"Rule: {options.RuleMatcher} \"{options.RulePattern}\" -> {outbound.Name}; everything else direct.");
Console.WriteLine($"UDP: {options.UdpMode}, QUIC blocked: {options.BlockQuic}, IPv6: {options.Ipv6} (outbound {options.OutboundIpv6})");

// ---- engine ---------------------------------------------------------------------------------

// One container, wired exactly like the window's: the libraries register their own services and
// this application supplies the only thing they ask for, somewhere to put log lines.
using ServiceProvider services = new ServiceCollection()
    .AddProxyDivert(config.DiagnosticLogPath, options.Verbose ? LogLevel.Debug : LogLevel.Information)
    .BuildServiceProvider();

RedirectEngine engine = services.GetRequiredService<RedirectEngine>();

engine.ProcessAttached += p => Console.WriteLine($"  [proc +] {Describe(p)}");
engine.ProcessDetached += p => Console.WriteLine($"  [proc -] {Describe(p)}");

engine.Connections.Updated += c =>
    Console.WriteLine($"  [open ] pid={c.ProcessId,-6} {c.Host ?? c.Destination.Address.ToString(),-40} " +
                      $"-> {c.OutboundName,-10} ({c.RouteReason})");
engine.Connections.Closed += c =>
    Console.WriteLine($"  [close] pid={c.ProcessId,-6} {c.Host ?? c.Destination.Address.ToString(),-40} " +
                      $"   up={c.BytesUp} down={c.BytesDown}{(c.Error is null ? "" : "  ERROR: " + c.Error)}");

try
{
    engine.Start(config);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start the engine: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine("Check that WinDivert.dll and WinDivert64.sys sit next to this exe.");
    return 1;
}

// Verbose means "show me what the engine is doing" — the same lines the trace file gets.
if (options.Verbose)
    services.GetRequiredService<InMemoryLogStore>().EntryAdded += entry => Console.WriteLine($"    {entry}");

// ---- what to redirect -------------------------------------------------------------------------

ISuspendedProcessLauncher launcher = services.GetRequiredService<ISuspendedProcessLauncher>();
IProcessFinder processFinder = services.GetRequiredService<IProcessFinder>();
ISuspendedProcess? launched = null;
try
{
    foreach (uint pid in options.Pids)
    {
        ProcessInfo? info = processFinder.FindById(pid);
        if (info is null)
        {
            Console.Error.WriteLine($"No process with id {pid}.");
            continue;
        }
        engine.AttachProcessId(pid, policy.Id, includeChildren: true);
    }

    if (options.LaunchExe != null)
    {
        launched = launcher.Launch(options.LaunchExe, options.LaunchArgs);
        Console.WriteLine($"Launched suspended: pid={launched.Pid} \"{options.LaunchExe}\" {options.LaunchArgs}");
        // Attach while it is still frozen — that is the whole point of launching suspended.
        engine.AttachProcessId(launched.Pid, policy.Id, includeChildren: true);
        launched.Resume();
        Console.WriteLine($"Resumed pid={launched.Pid}");
    }

    Console.WriteLine(options.DurationSeconds > 0
        ? $"Running for {options.DurationSeconds}s (Ctrl+C to stop early)…"
        : "Running (Ctrl+C to stop)…");
    Console.WriteLine();

    if (options.DurationSeconds > 0) exitCts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
    try { await Task.Delay(Timeout.Infinite, exitCts.Token); }
    catch (OperationCanceledException) { }
}
finally
{
    Console.WriteLine();
    Console.WriteLine("Stopping…");
    launched?.Dispose();
    engine.Stop();
    selfHosted?.Dispose();
}

// ---- summary ---------------------------------------------------------------------------------

IReadOnlyCollection<ConnectionInfo> history = engine.Connections.History;
Console.WriteLine();
Console.WriteLine($"Connections seen: {history.Count}");
foreach (var group in history.GroupBy(c => c.OutboundName).OrderBy(g => g.Key))
{
    long up = group.Sum(c => c.BytesUp);
    long down = group.Sum(c => c.BytesDown);
    Console.WriteLine($"  {group.Key,-12} {group.Count(),4} connections  up={up} down={down}");
}
foreach (ConnectionInfo failed in history.Where(c => c.Error != null).Take(10))
    Console.WriteLine($"  failed: {failed.Host ?? failed.Destination.ToString()} — {failed.Error}");

return 0;

static string Describe(TrackedProcess process)
    => $"pid={process.ProcessId,-6} {process.Name}{(process.IsChild ? " (child)" : "")}";

static OutboundKind KindFromUrl(string url)
{
    string scheme = url.Split(':')[0].ToLowerInvariant();
    return scheme switch
    {
        "http" or "https" => OutboundKind.HttpProxy,
        "socks4" or "socks4a" => OutboundKind.Socks4,
        "socks5" or "socks" => OutboundKind.Socks5,
        _ => throw new FormatException($"Unsupported proxy scheme '{scheme}'."),
    };
}

static bool IsElevated()
{
    try
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch
    {
        return false;
    }
}
