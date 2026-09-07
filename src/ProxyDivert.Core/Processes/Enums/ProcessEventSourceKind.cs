namespace ProxyDivert.Core.Processes.Enums;

/// <summary>Where the process table hears about processes starting and stopping.</summary>
public enum ProcessEventSourceKind
{
    /// <summary>
    /// The kernel's own ETW provider. The shortest path there is: the event arrives within a
    /// millisecond of the process being created, in this process, with no service in between.
    /// </summary>
    Etw = 0,

    /// <summary>
    /// WMI process traces — the same kernel events, after WMI has repackaged them and handed them
    /// over one at a time. Kept because it needs no ETW session, so it still works where one
    /// cannot be created.
    /// </summary>
    Wmi = 1,
}
