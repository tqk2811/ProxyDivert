using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Process start/stop events, straight from the kernel's own ETW provider.
/// </summary>
/// <remarks>
/// This replaces the WMI process traces the table used to follow. Both carry the same facts — WMI's
/// Win32_ProcessStartTrace is itself fed by this provider — but WMI adds a COM round trip and a
/// service in between, and hands the events of one watcher over strictly in turn. Reading the
/// provider directly is one session in this process, with the events arriving within a millisecond
/// of the kernel creating the process.
///
/// Only the manifest provider is enabled, never the "NT Kernel Logger": that one is a single
/// machine-wide session, so using it would mean fighting whatever profiler the user happens to have
/// open. A private session with its own name coexists with anything else.
///
/// Threading: <see cref="Started"/> and <see cref="Stopped"/> are raised on the session's own pump
/// thread. A handler that blocks delays the next event, so handlers must be short.
/// </remarks>
public sealed class EtwProcessEventSource : IProcessEventSource
{
    /// <summary>The kernel provider carrying process, thread and image events.</summary>
    private const string ProviderName = "Microsoft-Windows-Kernel-Process";

    /// <summary>WINEVENT_KEYWORD_PROCESS — process start/stop only, not threads or images.</summary>
    private const ulong ProcessKeyword = 0x10;

    // Event ids from the provider's manifest.
    private const int ProcessStartEventId = 1;
    private const int ProcessStopEventId = 2;

    /// <summary>
    /// Fixed rather than unique per run, so a session left behind by a crash is found and stopped
    /// instead of accumulating. Only one copy of this tool is expected to run at a time.
    /// </summary>
    private const string SessionName = "ProxyDivert-Process";

    private readonly ILogger _logger;
    private TraceEventSession? _session;
    private Task? _pump;

    public EtwProcessEventSource(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "ETW";

    public event Action<ProcessStartedEvent>? Started;

    public event Action<uint>? Stopped;

    /// <summary>
    /// Opens the session and starts pumping. False when ETW is not available — no elevation, or
    /// the session could not be created — and the caller then falls back to polling.
    /// </summary>
    public bool TryStart()
    {
        try
        {
            StopSessionLeftBehind();

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(ProviderName, TraceEventLevel.Informational, ProcessKeyword);
            _session.Source.Dynamic.All += OnEvent;

            // Process() blocks until the session is disposed, so it gets a thread of its own —
            // and LongRunning is what actually asks for one. Task.Run, which this used to say,
            // borrows a thread-pool worker and never gives it back, which is a worker the pool has
            // to replace at its own slow rate while everything else in the process waits.
            _pump = Task.Factory.StartNew(
                PumpAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            _logger.LogDebug("the process table follows the {Provider} ETW provider", ProviderName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the ETW process session could not be started");
            Dispose();
            return false;
        }
    }

    // A session outlives the process that made it: killed in the debugger, or crashed, and its
    // session keeps running and keeps the name taken. Attaching to it and stopping it is the only
    // way to get the name back.
    private void StopSessionLeftBehind()
    {
        try
        {
            using var existing = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
            existing.Stop();
            _logger.LogDebug("stopped an ETW session left behind by an earlier run");
        }
        catch
        {
            // The normal case: no such session. Attach throws rather than answering null.
        }
    }

    private void PumpAsync()
    {
        try { _session?.Source.Process(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the ETW process session stopped pumping");
        }
    }

    private void OnEvent(TraceEvent data)
    {
        if (!string.Equals(data.ProviderName, ProviderName, StringComparison.Ordinal)) return;

        try
        {
            switch ((int)data.ID)
            {
                case ProcessStartEventId:
                {
                    uint pid = ReadUInt32(data, "ProcessID");
                    if (pid == 0) return;
                    Started?.Invoke(new ProcessStartedEvent(
                        pid,
                        ReadUInt32(data, "ParentProcessID"),
                        ReadImageName(data),
                        ReadUInt32(data, "SessionID")));
                    break;
                }

                case ProcessStopEventId:
                {
                    uint pid = ReadUInt32(data, "ProcessID");
                    if (pid != 0) Stopped?.Invoke(pid);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "handling an ETW process event failed");
        }
    }

    // The manifest calls it ImageName and fills it with the NT path
    // ("\Device\HarddiskVolume3\Windows\notepad.exe"). Everything downstream wants the file name;
    // the real path is read from the process's own handle a moment later anyway.
    private static string ReadImageName(TraceEvent data)
    {
        string? image = data.PayloadByName("ImageName") as string;
        if (string.IsNullOrEmpty(image)) return string.Empty;

        int slash = image.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 && slash + 1 < image.Length ? image.Substring(slash + 1) : image;
    }

    // Payload fields are typed as signed ints in the manifest, and a value that is missing or of an
    // unexpected type is not worth an exception on the event path.
    private static uint ReadUInt32(TraceEvent data, string name)
    {
        try
        {
            object? value = data.PayloadByName(name);
            return value switch
            {
                int i => unchecked((uint)i),
                uint u => u,
                long l => unchecked((uint)l),
                _ => 0u,
            };
        }
        catch
        {
            return 0u;
        }
    }

    public void Dispose()
    {
        // Disposing the session is what makes Process() return, so it comes first.
        try { _session?.Dispose(); } catch { }
        _session = null;
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _pump = null;
    }
}
