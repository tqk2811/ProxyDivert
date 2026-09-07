using ProxyDivert.Core.Processes.Enums;

namespace ProxyDivert.Core.Processes.Interfaces;

/// <summary>
/// Builds the event source the user picked. A factory rather than two injected instances because
/// only one of them is subscribed at a time, and starting one costs a trace session or a WMI
/// watcher — neither worth holding open for the source that was not chosen.
/// </summary>
public interface IProcessEventSourceFactory
{
    IProcessEventSource Create(ProcessEventSourceKind kind);
}
