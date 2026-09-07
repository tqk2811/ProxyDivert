using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ProxyDivert.Wpf.Services;

/// <summary>
/// Runs an action on the UI thread at most once per burst of requests. Any number of calls to
/// <see cref="Request"/> made before the queued run starts collapse into that one run; a call made
/// while it is running queues exactly one more, so the last state is always shown.
/// </summary>
/// <remarks>
/// For an engine event that fires once per process. A save that re-evaluates sixty browser
/// processes raised sixty events in a row, and rebuilding the process tree for each of them kept
/// the window busy long after the engine itself was done — it is the rebuild that is expensive,
/// not the event. The action always reads the engine's current state, so running it once after
/// the burst shows exactly what running it sixty times would have.
/// </remarks>
public sealed class CoalescedDispatcherAction
{
    private readonly Action _action;
    private readonly DispatcherPriority _priority;

    // 1 while a run is queued and has not started. Interlocked because the requests come from
    // engine threads and the reset happens on the UI thread.
    private int _pending;

    /// <param name="priority">
    /// Background by default: the refresh is for the eye, and input or layout that is already
    /// waiting should go first.
    /// </param>
    public CoalescedDispatcherAction(Action action, DispatcherPriority priority = DispatcherPriority.Background)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _priority = priority;
    }

    /// <summary>Asks for a run. Safe from any thread; a no-op while one is already queued.</summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // No window to draw on (shutting down, or a test without an Application): drop the
            // request rather than leave the flag set forever.
            Interlocked.Exchange(ref _pending, 0);
            return;
        }
        dispatcher.BeginInvoke(_priority, new Action(Run));
    }

    private void Run()
    {
        // Cleared before the action, not after: a request that arrives while the action is
        // reading the engine may be about a change the action has already missed.
        Interlocked.Exchange(ref _pending, 0);
        _action();
    }
}
