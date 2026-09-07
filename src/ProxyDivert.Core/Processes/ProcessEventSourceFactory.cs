using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Processes.Interfaces;

namespace ProxyDivert.Core.Processes;

/// <inheritdoc />
public sealed class ProcessEventSourceFactory : IProcessEventSourceFactory
{
    private readonly ILogger _logger;

    public ProcessEventSourceFactory(ILogger logger)
    {
        _logger = logger;
    }

    public IProcessEventSource Create(ProcessEventSourceKind kind) => kind switch
    {
        ProcessEventSourceKind.Wmi => new WmiProcessEventSource(_logger),
        _ => new EtwProcessEventSource(_logger),
    };
}
