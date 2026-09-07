using System;
using System.Management;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Process start/stop events as WMI reports them (Win32_ProcessStartTrace / Win32_ProcessStopTrace).
/// </summary>
/// <remarks>
/// The same kernel events <see cref="EtwProcessEventSource"/> reads, arriving by a longer road: the
/// kernel writes them to ETW, the WMI service reads them there and republishes them, and a COM
/// callback brings them here. That costs latency and hands the events of one watcher over strictly
/// in turn, which is why it is no longer the first choice — but it needs no trace session of its
/// own, so it stays as the answer for a machine where one cannot be created.
/// </remarks>
public sealed class WmiProcessEventSource : IProcessEventSource
{
    private readonly ILogger _logger;
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;

    public WmiProcessEventSource(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "WMI";

    public event Action<ProcessStartedEvent>? Started;

    public event Action<uint>? Stopped;

    public bool TryStart()
    {
        try
        {
            _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the WMI process watcher failed to start");
            Dispose();
            return false;
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value);
            if (pid == 0) return;

            Started?.Invoke(new ProcessStartedEvent(
                pid,
                Convert.ToUInt32(e.NewEvent.Properties["ParentProcessID"].Value),
                e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? string.Empty,
                TryReadUInt32(e.NewEvent, "SessionID")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "handling a process-start event failed");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value);
            if (pid != 0) Stopped?.Invoke(pid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "handling a process-stop event failed");
        }
    }

    // A property WMI may or may not have put on the event. Nothing here is worth an exception.
    private static uint TryReadUInt32(ManagementBaseObject wmiEvent, string name)
    {
        try { return Convert.ToUInt32(wmiEvent[name] ?? 0u); }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException
            or FormatException or OverflowException)
        {
            return 0u;
        }
    }

    public void Dispose()
    {
        try { _startWatcher?.Stop(); } catch { }
        try { _startWatcher?.Dispose(); } catch { }
        try { _stopWatcher?.Stop(); } catch { }
        try { _stopWatcher?.Dispose(); } catch { }
        _startWatcher = null;
        _stopWatcher = null;
    }
}
