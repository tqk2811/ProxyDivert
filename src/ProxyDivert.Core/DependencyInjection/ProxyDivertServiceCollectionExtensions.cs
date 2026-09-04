using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Logging;
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

        // ConfigStore is deliberately absent: a host has to read its configuration BEFORE building
        // this container, because the configuration is what says where the trace file goes.
        services.TryAddSingleton<RedirectEngine>();
        return services;
    }
}
