using System;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// Stands in for ETW or WMI: the test decides when a process event arrives, and the same object is
/// handed back as the factory so the inventory subscribes to it.
/// </summary>
/// <remarks>
/// It also keeps a started table off the operating system's event stream — and, because it always
/// starts, off the fast poll a table falls back to when no source will, which would sweep the fake
/// machine from another thread every 750ms.
/// </remarks>
internal sealed class FakeProcessEventSource : IProcessEventSource, IProcessEventSourceFactory
{
    public string Name => "fake";

    public event Action<ProcessStartedEvent>? Started;
    public event Action<uint>? Stopped;

    public IProcessEventSource Create(ProcessEventSourceKind kind) => this;
    public bool TryStart() => true;
    public void Dispose() { }

    public void RaiseStarted(ProcessStartedEvent started) => Started?.Invoke(started);
    public void RaiseStopped(uint processId) => Stopped?.Invoke(processId);
}
