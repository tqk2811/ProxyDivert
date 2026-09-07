using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// The machine's process list, in memory, kept current for as long as the application runs: process
/// id, executable path, command line and parent for everything running, whether or not anything is
/// being redirected yet.
/// </summary>
/// <remarks>
/// This exists because the two things the tool does with processes have completely different
/// lifetimes. KNOWING what runs on this machine is worth doing from the moment the window opens —
/// it costs about thirty milliseconds to learn and almost nothing to maintain. DECIDING which of
/// them to redirect only matters while the engine runs, and it is a question that should be
/// answered out of memory rather than by going back to the operating system. So the two are
/// separate: this class collects, <see cref="ProcessRuleTracker"/> decides.
///
/// Where the entries come from, in the order they are set up:
///
///   * process start/stop events, hooked first, because a process that starts during the first
///     sweep must not fall into the gap between the two. Which source they come from — the
///     kernel's ETW provider, or WMI — is the user's choice and changes nothing here;
///   * one full sweep, which costs a single syscall for the list plus one handle per process for
///     the details — about 30ms on a machine running 640 processes;
///   * a periodic reconcile that compares the table against a fresh listing. It is what makes the
///     table self-healing: a dropped event, or a process that started while the machine was
///     asleep, is corrected within one interval instead of never.
///
/// When no event source can be started at all, the reconcile simply runs far more often and
/// becomes the only source. Nothing else changes, which is why there is one loop here rather than two.
///
/// Threading: every member is safe from any thread. The two events are raised on whichever thread
/// noticed — the event source's pump, or the reconcile loop — and a handler that blocks delays the next
/// process being reported, so handlers are expected to be short.
/// </remarks>
public sealed class ProcessInventory : IDisposable
{
    /// <summary>How often the table is compared against a fresh listing while events are working.</summary>
    /// <remarks>
    /// A listing is ~12ms, and a reconcile only reads details for processes it has not seen before,
    /// so the steady-state cost is about a quarter of a percent of one core. That buys an upper
    /// bound on how long the table can stay wrong after a lost event, which is worth far more than
    /// the cycles it costs.
    /// </remarks>
    private const int ReconcileIntervalMs = 5_000;

    /// <summary>How often the table is rebuilt when no event source works and the loop is all there is.</summary>
    private const int PollIntervalMs = 750;

    private readonly ILogger<ProcessInventory> _logger;
    private readonly IProcessLister _lister;
    private readonly IProcessDetailsReader _detailsReader;
    private readonly ConcurrentDictionary<uint, ProcessSnapshot> _processes
        = new ConcurrentDictionary<uint, ProcessSnapshot>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    // Held while reconciling. Two reconciles at once would each see the other half-applied and
    // report the same process twice; the lock turns the second one into a wait that finds the job
    // already done.
    private readonly object _reconcileLock = new object();

    private readonly IProcessEventSourceFactory _eventSources;

    private IProcessEventSource? _events;
    private Task? _loopTask;
    private bool _started;

    /// <summary>A process appeared. Raised once per process.</summary>
    public event Action<ProcessSnapshot>? ProcessStarted;

    /// <summary>A process is gone. The snapshot handed over is the last one known about it.</summary>
    public event Action<ProcessSnapshot>? ProcessStopped;

    /// <summary>True while process events are arriving; false while the table is only polled.</summary>
    public bool IsUsingEvents { get; private set; }

    /// <summary>
    /// Where events are being read from. The user picks this in the settings; it is only a request
    /// until <see cref="Start"/>, and a source that will not start falls back to the other one and
    /// then to polling, so this says what was ASKED for and <see cref="ActiveEventSource"/> says
    /// what answered.
    /// </summary>
    public ProcessEventSourceKind EventSource { get; private set; } = ProcessEventSourceKind.Etw;

    /// <summary>The source actually delivering events, or null while the table is polled.</summary>
    public ProcessEventSourceKind? ActiveEventSource { get; private set; }

    public ProcessInventory(
        ILogger<ProcessInventory> logger,
        IProcessLister? lister = null,
        IProcessDetailsReader? detailsReader = null,
        IProcessEventSourceFactory? eventSources = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lister = lister ?? new NativeProcessLister();
        _detailsReader = detailsReader ?? new NativeProcessDetailsReader();
        _eventSources = eventSources ?? new ProcessEventSourceFactory(_logger);
    }

    /// <summary>
    /// Switches to another event source, before or after <see cref="Start"/>. Takes effect at once:
    /// the current source is torn down and the new one subscribed, and the table is reconciled
    /// afterwards so nothing that happened during the swap is lost.
    /// </summary>
    public void UseEventSource(ProcessEventSourceKind kind)
    {
        if (EventSource == kind && (!_started || IsUsingEvents)) return;

        EventSource = kind;
        if (!_started) return;

        StopEvents();
        IsUsingEvents = TryStartEvents();
        Reconcile();
    }

    /// <summary>How many processes the table holds.</summary>
    public int Count => _processes.Count;

    /// <summary>Everything running, as of now.</summary>
    public IReadOnlyCollection<ProcessSnapshot> All => _processes.Values.ToList();

    /// <summary>One process, or null when the table has never heard of it (or it has exited).</summary>
    public ProcessSnapshot? Get(uint processId)
        => _processes.TryGetValue(processId, out ProcessSnapshot? snapshot) ? snapshot : null;

    public bool TryGet(uint processId, out ProcessSnapshot? snapshot)
        => _processes.TryGetValue(processId, out snapshot);

    /// <summary>
    /// Starts collecting. The event hooks go up first and the first sweep runs before this returns,
    /// so a caller that starts the engine on the next line finds a complete table.
    /// </summary>
    /// <remarks>
    /// Hooking before sweeping is what closes the gap a process could start in. Sweeping
    /// synchronously is affordable precisely because of how the details are read — thirty
    /// milliseconds, against the second and a half the same sweep cost through WMI, which is why it
    /// used to be pushed onto a background thread.
    /// </remarks>
    public void Start()
    {
        if (_started) throw new InvalidOperationException("Already started");
        _started = true;

        // Being an administrator is not enough on its own: the privilege sits in the token DISABLED,
        // and without it every process of another user reads as pathless and argumentless.
        new DebugPrivilege(_logger).TryEnable();

        IsUsingEvents = TryStartEvents();

        Reconcile();
        _logger.LogDebug("the process table starts with {Count} processes", _processes.Count);

        _loopTask = Task.Run(() => ReconcileLoop(_cts.Token));
    }

    /// <summary>Compares the table against the machine as it is right now. Safe from any thread.</summary>
    public void Refresh() => Reconcile();

    // One pass: everything running goes into the table, everything in the table that is no longer
    // running comes out, and a pid handed to a different program counts as the old process ending
    // and a new one starting.
    private void Reconcile()
    {
        lock (_reconcileLock)
        {
            IReadOnlyList<ProcessSnapshot> listed;
            try
            {
                listed = _lister.ListAll();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "listing the processes on this machine failed");
                return;
            }
            if (listed.Count == 0) return;

            var alive = new HashSet<uint>(listed.Count);

            foreach (ProcessSnapshot process in listed)
            {
                alive.Add(process.ProcessId);

                if (!_processes.TryGetValue(process.ProcessId, out ProcessSnapshot? known))
                {
                    Admit(process);
                    continue;
                }

                // Same pid, different process: the stop event was missed. Answering for the new one
                // out of the old one's entry is worse than anything a missed event costs.
                if (IsDifferentProcess(known, process))
                {
                    Retire(process.ProcessId, "its pid was handed to another program");
                    Admit(process);
                    continue;
                }

                // Opened once and refused — usually a process that had only just started when its
                // event arrived, before the kernel had published its command line.
                if (!known.DetailsRead) _processes.TryUpdate(process.ProcessId, WithDetails(known), known);
            }

            foreach (uint pid in _processes.Keys)
            {
                if (!alive.Contains(pid)) Retire(pid, "exited");
            }
        }
    }

    // Two entries under one pid describe different processes when the kernel says they started at
    // different instants. An unknown instant on either side is not evidence of anything, so the name
    // is the fallback — weaker, but it still catches the common case of one program's pid going to
    // another.
    private static bool IsDifferentProcess(ProcessSnapshot known, ProcessSnapshot listed)
    {
        if (known.StartedUtc != DateTime.MinValue && listed.StartedUtc != DateTime.MinValue)
            return known.StartedUtc != listed.StartedUtc;

        return !string.Equals(known.Name, listed.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void Admit(ProcessSnapshot process)
    {
        ProcessSnapshot complete = WithDetails(process);
        if (!_processes.TryAdd(complete.ProcessId, complete)) return;
        Raise(ProcessStarted, complete, nameof(ProcessStarted));
    }

    private void Retire(uint processId, string reason)
    {
        if (!_processes.TryRemove(processId, out ProcessSnapshot? gone)) return;
        _logger.LogTrace("pid={Pid} name={Name} left the process table ({Reason})", processId, gone.Name, reason);
        Raise(ProcessStopped, gone, nameof(ProcessStopped));
    }

    // The path and the command line, through one handle. A process that opens but answers nothing
    // is still recorded as asked, so nothing asks again; a process that would not open at all is
    // left unread, because that usually means it is younger than the event announcing it and the
    // next reconcile should try again.
    private ProcessSnapshot WithDetails(ProcessSnapshot process)
    {
        ProcessDetails details;
        try
        {
            details = _detailsReader.Read(process.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "reading the details of pid={Pid} failed", process.ProcessId);
            return process;
        }

        if (!details.AnythingRead) return process;

        ProcessSnapshot complete = process.WithDetails(details.ExecutablePath, details.CommandLine);

        // Only taken when the listing had none: the listing reads the same kernel clock, and one
        // instant per process is what makes the pid comparison above trustworthy.
        return complete.StartedUtc == DateTime.MinValue && details.StartedUtc != DateTime.MinValue
            ? complete with { StartedUtc = details.StartedUtc }
            : complete;
    }

    // The source the user asked for, and the other one if it will not start: both carry the same
    // kernel events, so falling back costs latency rather than correctness. Polling is what is left
    // when neither answers, and it is a far bigger step down — a process is then noticed up to one
    // poll interval after it started, by which time its first connection may already be out.
    private bool TryStartEvents()
    {
        if (TryStartEventSource(EventSource)) return true;

        ProcessEventSourceKind fallback = EventSource == ProcessEventSourceKind.Etw
            ? ProcessEventSourceKind.Wmi
            : ProcessEventSourceKind.Etw;

        if (TryStartEventSource(fallback))
        {
            _logger.LogWarning(
                "{Wanted} could not be started, so the process table is following {Fallback} instead",
                EventSource, fallback);
            return true;
        }

        _logger.LogWarning(
            "no process event source could be started, so the table is rebuilt every {IntervalMs}ms instead — a new process is noticed later",
            PollIntervalMs);
        return false;
    }

    private bool TryStartEventSource(ProcessEventSourceKind kind)
    {
        IProcessEventSource source;
        try { source = _eventSources.Create(kind); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the {Kind} process event source could not be built", kind);
            return false;
        }

        source.Started += OnProcessStarted;
        source.Stopped += OnProcessStopped;
        if (!source.TryStart())
        {
            source.Started -= OnProcessStarted;
            source.Stopped -= OnProcessStopped;
            source.Dispose();
            return false;
        }

        _events = source;
        ActiveEventSource = kind;
        _logger.LogDebug("the process table follows {Source} process events", source.Name);
        return true;
    }

    private void StopEvents()
    {
        IProcessEventSource? events = _events;
        _events = null;
        ActiveEventSource = null;
        IsUsingEvents = false;
        if (events is null) return;

        events.Started -= OnProcessStarted;
        events.Stopped -= OnProcessStopped;
        try { events.Dispose(); } catch { }
    }

    private void OnProcessStarted(ProcessStartedEvent started)
    {
        // The stop event for whatever held this pid before may never have arrived.
        if (_processes.ContainsKey(started.ProcessId))
            Retire(started.ProcessId, "its pid was handed to another program");

        Admit(new ProcessSnapshot
        {
            ProcessId = started.ProcessId,
            Name = string.IsNullOrEmpty(started.ImageName) ? $"pid {started.ProcessId}" : started.ImageName,
            ParentProcessId = started.ParentProcessId,
            SessionId = started.SessionId,
            // Filled in from the handle the detail read opens; the event does not carry it.
            StartedUtc = DateTime.MinValue,
        });
    }

    private void OnProcessStopped(uint pid) => Retire(pid, "exited");

    private async Task ReconcileLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int delayMs = IsUsingEvents ? ReconcileIntervalMs : PollIntervalMs;
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try { Reconcile(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "rebuilding the process table failed");
            }
        }
    }

    // A subscriber that throws must not stop the table from being maintained, and must not stop the
    // other subscribers from hearing about the process either.
    private void Raise(Action<ProcessSnapshot>? handler, ProcessSnapshot process, string eventName)
    {
        if (handler is null) return;
        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try { ((Action<ProcessSnapshot>)subscriber)(process); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "a handler of {Event} failed for pid={Pid}", eventName, process.ProcessId);
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        StopEvents();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _processes.Clear();
    }
}
