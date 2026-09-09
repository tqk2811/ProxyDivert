using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TqkLibrary.WinDivert.Redirect.Interfaces;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Tells the driver which processes to capture, from one thread of its own.
/// </summary>
/// <remarks>
/// Attaching a pid is not a bookkeeping change: outside machine-wide mode it opens a WinDivert
/// SOCKET handle for that process and starts a pump for it. That used to happen on whichever thread
/// reported the process, which is the ETW session's own callback thread — the one whose contract
/// says handlers must be short, because a slow one costs dropped events, and a dropped
/// process-start event is a program that is never redirected at all. A browser start is sixty of
/// these in a few seconds.
///
/// One reader, and the queue is ordered, so attach-then-detach of the same pid cannot arrive the
/// other way round. Everything about a pid stays on this one thread, which is the point: the
/// driver's process list has exactly one writer.
///
/// The cost of moving work off a thread is that callers who needed it done before they carried on
/// have to say so, and <see cref="DrainAsync"/> is how. Three do: starting the engine (a browser
/// that was already open must be captured before Start returns), applying a configuration (the
/// escaped-flow reset that follows reads the driver's flow table), and attaching a process that was
/// launched suspended (it is resumed the moment the call returns, and its first SYN is out).
/// </remarks>
internal sealed class TrackedPidQueue : IAsyncDisposable
{
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IProcessRedirector _redirector;
    private readonly ILogger _logger;
    private readonly Task _consumer;

    public TrackedPidQueue(IProcessRedirector redirector, ILogger logger)
    {
        _redirector = redirector ?? throw new ArgumentNullException(nameof(redirector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consumer = Task.Run(ConsumeAsync);
    }

    public void Attach(uint processId) => _changes.Writer.TryWrite(new Change(processId, attach: true));

    public void Detach(uint processId) => _changes.Writer.TryWrite(new Change(processId, attach: false));

    /// <summary>
    /// Completes once everything queued before this call has reached the driver. A marker in the
    /// queue rather than a look at how empty it is: there is one reader and the queue is ordered, so
    /// reaching the marker is proof that everything ahead of it is done.
    /// </summary>
    public Task DrainAsync()
    {
        // Asynchronous continuations: whoever is waiting must not resume on the consumer's thread
        // and go on to do their own work there — the queue would stop draining behind them.
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _changes.Writer.TryWrite(new Change(barrier)) ? barrier.Task : Task.CompletedTask;
    }

    /// <summary>
    /// Stops the queue once what is already in it has been applied. Called before the redirector is
    /// disposed: a pid still on its way to a driver handle that has been closed is an exception
    /// thrown where nobody is watching.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _changes.Writer.TryComplete();
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the tracked-process queue did not stop cleanly");
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Change change in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (change.Barrier != null)
            {
                change.Barrier.TrySetResult(true);
                continue;
            }

            try
            {
                if (change.Attach) _redirector.AddTrackedProcessId(change.ProcessId);
                else _redirector.RemoveTrackedProcessId(change.ProcessId);
            }
            catch (Exception ex)
            {
                // One pid failing says nothing about the next one, and there is no caller left to
                // throw at: whoever asked for this went on with their work several steps ago.
                _logger.LogWarning(
                    ex, "{Verb} pid={Pid} failed", change.Attach ? "attaching" : "detaching", change.ProcessId);
            }
        }
    }

    private readonly struct Change
    {
        public Change(uint processId, bool attach)
        {
            ProcessId = processId;
            Attach = attach;
            Barrier = null;
        }

        public Change(TaskCompletionSource<bool> barrier)
        {
            ProcessId = 0;
            Attach = false;
            Barrier = barrier;
        }

        public uint ProcessId { get; }
        public bool Attach { get; }
        public TaskCompletionSource<bool>? Barrier { get; }
    }
}
