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
using ProxyDivert.Core.Hosting;
using ProxyDivert.Core.Outbounds.Models;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
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
    OutboundId = outbound.Id,
    UdpMode = options.UdpMode,
    BlockQuic = options.BlockQuic,
};
policy.Rules.Add(new RoutingRule
{
    Id = Guid.NewGuid(),
    Matcher = options.RuleMatcher,
    Pattern = options.RulePattern,
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
    // The command line takes one process pattern, which is the simplest shape of a filter: a
    // single condition in a group of one. Anything more elaborate is written in the window.
    config.ProcessRules.Add(new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = options.ProcessPattern,
        Condition = new ConditionGroup
        {
            Children =
            {
                new ProcessNameCondition
                {
                    Matcher = ProcessMatcherType.ExeName,
                    Pattern = options.ProcessPattern,
                },
            },
        },
        PolicyIds = { policy.Id },
        IncludeChildren = true,
    });
}

// Assembled by hand rather than loaded, so it has never been through the checks a saved file gets.
// The same call the store makes on load: it costs nothing here and means the engine is handed one
// shape of configuration whichever way the tool was started.
config.Normalize();

Console.WriteLine($"Rule: {options.RuleMatcher} \"{options.RulePattern}\" -> {outbound.Name}; everything else direct.");
Console.WriteLine($"UDP: {options.UdpMode}, QUIC blocked: {options.BlockQuic}, IPv6: {options.Ipv6} (outbound {options.OutboundIpv6})");

// ---- engine ---------------------------------------------------------------------------------

// One container, wired exactly like the window's: the libraries register their own services and
// this application supplies the only thing they ask for, somewhere to put log lines.
await using ServiceProvider services = new ServiceCollection()
    .AddProxyDivert(config.DiagnosticLogPath, options.Verbose ? LogLevel.Debug : LogLevel.Information)
    .BuildServiceProvider();

// The same session the window drives, so a start here goes in the same order: tunnels switched on,
// process table collecting, driver open, tunnels dialled. No store is registered, so it writes no
// file — this configuration came from the arguments and ends with them.
ProxyDivertSession session = services.GetRequiredService<ProxyDivertSession>();
RedirectEngine engine = session.Engine;

engine.ProcessAttached += p => Console.WriteLine($"  [proc +] {Describe(p)}");
engine.ProcessDetached += p => Console.WriteLine($"  [proc -] {Describe(p)}");

engine.Connections.Updated += c =>
    Console.WriteLine($"  [open ] pid={c.ProcessId,-6} {c.Host ?? c.Destination.Address.ToString(),-40} " +
                      $"-> {c.OutboundName,-10} ({c.RouteReason})");
engine.Connections.Closed += c =>
    Console.WriteLine($"  [close] pid={c.ProcessId,-6} {c.Host ?? c.Destination.Address.ToString(),-40} " +
                      $"   up={c.BytesUp} down={c.BytesDown}{(c.Error is null ? "" : "  ERROR: " + c.Error)}");

// A command-line run switches on whatever its own configuration routes through a VPN and lets the
// container drop the tunnels when it exits; the window keeps its tunnels across runs. Returns with
// the tunnels still coming up — connections a rule routes through one of them are held until it is,
// not sent out direct. See ProxyDivertSession.StartAsync.
try
{
    await session.StartAsync(config);
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
ISuspendedProcess? launched = null;
try
{
    foreach (uint pid in options.Pids)
    {
        ProcessSnapshot? info = session.Processes.Get(pid);
        if (info is null)
        {
            Console.Error.WriteLine($"No process with id {pid}.");
            continue;
        }
        await engine.AttachProcessIdAsync(pid, policy.Id, includeChildren: true);
    }

    if (options.LaunchExe != null)
    {
        launched = launcher.Launch(options.LaunchExe, options.LaunchArgs);
        Console.WriteLine($"Launched suspended: pid={launched.Pid} \"{options.LaunchExe}\" {options.LaunchArgs}");
        // Attach while it is still frozen — that is the whole point of launching suspended.
        await engine.AttachProcessIdAsync(launched.Pid, policy.Id, includeChildren: true);
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
catch (Exception ex)
{
    // --launch on a path that is not there is the everyday one, and it used to come back as a
    // stack trace and whatever exit code the runtime picked for an unhandled exception. A tool
    // that is meant to be run from a script says what went wrong in one line and returns 1.
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
finally
{
    Console.WriteLine();
    Console.WriteLine("Stopping…");
    launched?.Dispose();
    await session.StopAsync();
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

// The scheme is read where every other reading of an address box happens, so "socks5://" means the
// same thing on the command line as it does in the window.
static OutboundKind KindFromUrl(string url)
    => OutboundAddress.TryReadProxyKind(url, out OutboundKind kind)
        ? kind
        : throw new FormatException(
            $"Unsupported proxy scheme in '{url}'. Use http://, socks4:// or socks5://.");

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
