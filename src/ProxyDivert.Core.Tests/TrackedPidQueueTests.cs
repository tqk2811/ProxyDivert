using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Engine;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Attaching a pid opens a driver handle and starts a pump for it. It used to happen on the thread
// that reported the process — the ETW session's own callback thread, which is asked to return
// quickly because a slow handler costs dropped events, and a dropped process-start event is a
// program that is never redirected at all.
public class TrackedPidQueueTests
{
    private static (TrackedPidQueue Queue, FakeProcessRedirector Redirector) Build()
    {
        var redirector = new FakeProcessRedirector();
        return (new TrackedPidQueue(redirector, NullLogger.Instance), redirector);
    }

    [Fact]
    public async Task WhatWasAskedFor_ReachesTheDriverInTheOrderItWasAsked()
    {
        (TrackedPidQueue queue, FakeProcessRedirector redirector) = Build();
        await using (queue)
        {
            queue.Attach(100);
            queue.Attach(200);
            queue.Detach(100);

            await queue.DrainAsync();
        }

        // Order is the whole reason this is a queue and not a thread pool: attach-then-detach of
        // one pid arriving the other way round leaves a process captured that nobody is watching.
        Assert.Equal(new[] { "add:100", "add:200", "remove:100" }, redirector.PidCalls);
        Assert.Equal(new uint[] { 200 }, redirector.TrackedProcessIds);
    }

    [Fact]
    public async Task AskingForAnAttach_ReturnsWithoutWaitingForTheDriver()
    {
        (TrackedPidQueue queue, FakeProcessRedirector redirector) = Build();
        using var held = new ManualResetEventSlim(false);
        redirector.BeforePidCall = _ => held.Wait(TimeSpan.FromSeconds(5));

        try
        {
            queue.Attach(100);
            queue.Attach(200);

            // Both returned while the driver is still stuck inside the first one. That is the
            // property the ETW callback thread needs: it says what it saw and goes straight back to
            // reading events, however long a WinDivert handle takes to open.
            Assert.Empty(redirector.TrackedProcessIds);
        }
        finally
        {
            held.Set();
            await queue.DisposeAsync();
        }

        Assert.Equal(new[] { "add:100", "add:200" }, redirector.PidCalls);
    }

    [Fact]
    public async Task Draining_WaitsForTheDriverAndNotOnlyForTheQueue()
    {
        (TrackedPidQueue queue, FakeProcessRedirector redirector) = Build();
        using var held = new ManualResetEventSlim(false);
        redirector.BeforePidCall = _ => held.Wait(TimeSpan.FromSeconds(5));

        await using (queue)
        {
            queue.Attach(100);
            Task drained = queue.DrainAsync();

            // Still inside the driver call, so the barrier behind it cannot have been reached.
            Assert.False(drained.IsCompleted);

            held.Set();
            await drained;

            // This is what the suspended-launch flow depends on: by the time the caller's next line
            // runs, the pid is in the driver, not merely on its way there.
            Assert.Equal(new uint[] { 100 }, redirector.TrackedProcessIds);
        }
    }

    [Fact]
    public async Task OnePidThatCannotBeAttached_DoesNotStopTheNextOne()
    {
        (TrackedPidQueue queue, FakeProcessRedirector redirector) = Build();
        redirector.BeforePidCall = pid =>
        {
            if (pid == 100) throw new InvalidOperationException("no handle for this one");
        };

        await using (queue)
        {
            queue.Attach(100);
            queue.Attach(200);
            await queue.DrainAsync();
        }

        // There is nobody left to throw at — whoever asked went on with their work several steps
        // ago — so the failure is logged and the queue keeps going.
        Assert.Equal(new uint[] { 200 }, redirector.TrackedProcessIds);
    }

    [Fact]
    public async Task WhatWasQueuedBeforeTheEngineStopped_StillReachesTheDriver()
    {
        // The queue is disposed before the redirector is, so a pid still on its way cannot land on
        // a driver handle that has already been closed.
        (TrackedPidQueue queue, FakeProcessRedirector redirector) = Build();

        queue.Attach(100);
        queue.Attach(200);
        await queue.DisposeAsync();

        Assert.Equal(new[] { "add:100", "add:200" }, redirector.PidCalls);
    }

    [Fact]
    public async Task DrainingAQueueThatHasStopped_ReturnsRatherThanWaitingForever()
    {
        (TrackedPidQueue queue, _) = Build();
        await queue.DisposeAsync();

        await queue.DrainAsync();
    }
}
