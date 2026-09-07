using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Logging;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Vpn;
using TqkLibrary.WinDivert.ProcessControl.DependencyInjection;
using TqkLibrary.WinDivert.Redirect.DependencyInjection;

namespace ProxyDivert.Core.DependencyInjection;

/// <summary>
/// The application's composition root, shared by the window and the command line so both run the
/// same engine wired the same way.
/// </summary>
public static class ProxyDivertServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine, the configuration store, and the logging destination — the in-memory
    /// store the log pane reads plus, optionally, a trace file.
    /// </summary>
    /// <param name="logFilePath">
    /// Trace file, or null for none. It can be changed later through
    /// <see cref="AppLoggerProvider.SetFilePath"/> without restarting.
    /// </param>
    /// <param name="minimumLevel">
    /// How much detail reaches the sinks at all. Debug is the useful default: Trace turns on the
    /// per-packet lines, which are thousands per second on a busy connection.
    /// </param>
    public static IServiceCollection AddProxyDivert(
        this IServiceCollection services,
        string? logFilePath = null,
        LogLevel minimumLevel = LogLevel.Debug)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        // The libraries log through ILogger<T> and provide no sink, so this is where every line
        // from the packet path, the proxy library and this application converges.
        var store = new InMemoryLogStore();
        var provider = new AppLoggerProvider(store, logFilePath);
        services.TryAddSingleton(store);
        services.TryAddSingleton(provider);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddProvider(provider);
        });

        services.AddWinDivertRedirect();
        services.AddWinDivertProcessControl();

        // The process table is a singleton because it describes the MACHINE, not a run: the host
        // starts it when the application opens and it keeps itself current whether or not anything
        // is being redirected, so starting the engine costs a read rather than a rediscovery.
        services.TryAddSingleton<ProcessInventory>();

        // The outbound instances, and the VPN tunnels among them, belong to the APPLICATION rather
        // than to an engine run: a tunnel is the user's session with their provider, and switching
        // redirection off is not a reason to end it. Both are therefore singletons the engine
        // borrows — see RedirectEngine.Stop, which disposes neither.
        //
        // Registered through a factory because the constructor's other argument is a path, and the
        // container has no string to give it; it is set from the configuration on the first
        // ApplyOutbounds instead.
        services.TryAddSingleton(sp => new OutboundSourceFactory(sp.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<VpnConnectionKeeper>();

        // ConfigStore is deliberately absent: a host has to read its configuration BEFORE building
        // this container, because the configuration is what says where the trace file goes.
        services.TryAddSingleton<RedirectEngine>();
        return services;
    }
}
