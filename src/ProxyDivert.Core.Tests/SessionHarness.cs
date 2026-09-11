using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.DependencyInjection;
using ProxyDivert.Core.Hosting;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using TqkLibrary.WinDivert.Redirect.Interfaces;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// The container both hosts build, wired by <c>AddProxyDivert</c> exactly as they wire it, with the
/// driver and the machine's process list swapped for fakes.
/// </summary>
/// <remarks>
/// The engine that starts in it is the whole engine — tracker, pid queue, routers — so a test can
/// switch redirection on and off, and watch a process reach the driver, with no elevation.
/// </remarks>
internal sealed class SessionHarness : IAsyncDisposable
{
    // Something always runs: a listing that comes back empty is read as a failed listing.
    public FakeProcessMachine Machine { get; } = new FakeProcessMachine().Start(4, "System");
    public FakeProcessRedirectorFactory Redirectors { get; } = new FakeProcessRedirectorFactory();
    public ServiceProvider Services { get; }
    public ProxyDivertSession Session { get; }

    /// <param name="store">Where the session writes; none, as on the command line, when null.</param>
    /// <param name="configure">Anything else to register ahead of the application's own services.</param>
    public SessionHarness(ConfigStore? store = null, Action<IServiceCollection>? configure = null)
    {
        // Registered first: the libraries only add their own when none is there.
        IServiceCollection services = new ServiceCollection()
            .AddSingleton<IProcessRedirectorFactory>(Redirectors)
            .AddSingleton(_ => new ProcessInventory(
                NullLogger<ProcessInventory>.Instance, Machine, Machine, new FakeProcessEventSource()));
        if (store is not null) services.AddSingleton(store);
        configure?.Invoke(services);

        Services = services.AddProxyDivert(logFilePath: null).BuildServiceProvider();
        Session = Services.GetRequiredService<ProxyDivertSession>();
    }

    /// <summary>The driver the most recent run opened.</summary>
    public FakeProcessRedirector Redirector => Redirectors.Created[^1];

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    /// <summary>Direct and Block, one policy going Direct, and one filter sending chrome.exe to it.</summary>
    public static AppConfig Config(
        ProcessDetectionMode detection = ProcessDetectionMode.ProcessEvents,
        ProcessEventSourceKind source = ProcessEventSourceKind.Etw)
    {
        Outbound direct = Outbound.CreateDirect();
        var policy = new RoutingPolicy { Id = Guid.NewGuid(), Name = "test", OutboundId = direct.Id };

        return new AppConfig
        {
            Outbounds = { direct, Outbound.CreateBlock() },
            Policies = { policy },
            ProcessRules = { Filter("chrome.exe", policy.Id) },
            ProcessDetection = detection,
            ProcessEventSource = source,
        };
    }

    public static ProcessRule Filter(string exeName, Guid policyId)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = exeName,
            Condition = new ConditionGroup
            {
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = exeName },
                },
            },
            PolicyIds = { policyId },
        };
}
