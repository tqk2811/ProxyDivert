using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Processes.Enums;
using Xunit;
using static ProxyDivert.Core.Tests.SessionHarness;

namespace ProxyDivert.Core.Tests;

// What both hosts now share: a start, an apply and a stop, in the order they go in and on the queue
// they run on. The container is the real one, with the driver and the machine's process list
// swapped for fakes — see SessionHarness — so the engine that starts here is the whole engine.
public class ProxyDivertSessionTests
{
    [Fact]
    public async Task Switching_on_claims_what_was_already_running_all_the_way_into_the_driver()
    {
        await using var harness = new SessionHarness();
        harness.Machine.Start(100, "chrome.exe");

        await harness.Session.StartAsync(Config());

        Assert.True(harness.Session.IsRunning);
        // The table was collecting before the tracker read it, or the browser that was open before
        // the switch was flipped would not be found; and it is in the driver, not only on the way.
        Assert.NotNull(harness.Session.Processes.Get(100));
        Assert.Contains("add:100", harness.Redirector.PidCalls);
    }

    // The process table refuses a second Start, and it outlives every run.
    [Fact]
    public async Task Switching_off_and_on_again_reuses_the_process_table()
    {
        await using var harness = new SessionHarness();
        AppConfig config = Config();

        await harness.Session.StartAsync(config);
        await harness.Session.StopAsync();
        Assert.False(harness.Session.IsRunning);

        await harness.Session.StartAsync(config);

        Assert.True(harness.Session.IsRunning);
        Assert.Equal(2, harness.Redirectors.Created.Count);
    }

    // Starting without elevation is the everyday failure, and the switch has to work once the user
    // has fixed it — the table started on the way into the first attempt must not refuse the second.
    [Fact]
    public async Task A_start_the_driver_refused_can_be_tried_again()
    {
        await using var harness = new SessionHarness();
        harness.Redirectors.FailNextStart = new InvalidOperationException("the driver refused");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.StartAsync(Config()));
        Assert.False(harness.Session.IsRunning);

        await harness.Session.StartAsync(Config());

        Assert.True(harness.Session.IsRunning);
    }

    // The mode used to be read in three places; one host forgetting one of them gave a table
    // following ETW under a redirector watching every socket.
    [Theory]
    [InlineData(ProcessDetectionMode.ProcessEvents)]
    [InlineData(ProcessDetectionMode.NetworkSniff)]
    public async Task The_detection_mode_reaches_the_table_and_the_redirector_alike(ProcessDetectionMode mode)
    {
        await using var harness = new SessionHarness();

        await harness.Session.StartAsync(Config(mode, ProcessEventSourceKind.Wmi));

        bool followsEvents = mode == ProcessDetectionMode.ProcessEvents;
        ProcessEventSourceKind? expectedSource = followsEvents ? ProcessEventSourceKind.Wmi : null;
        Assert.Equal(expectedSource, harness.Session.Processes.EventSource);
        Assert.Equal(!followsEvents, harness.Redirectors.Options.Single().ShouldTrackProcess is not null);
    }

    // The settings tab changes the source while redirection is off, and the table is still running
    // from the last run: it has to switch now, not at the next start.
    [Fact]
    public async Task A_detection_change_between_runs_reaches_the_table_that_is_still_running()
    {
        await using var harness = new SessionHarness();
        AppConfig config = Config(ProcessDetectionMode.ProcessEvents);
        await harness.Session.StartAsync(config);
        await harness.Session.StopAsync();
        Assert.True(harness.Session.Processes.IsUsingEvents);

        config.ProcessDetection = ProcessDetectionMode.NetworkSniff;
        await harness.Session.UseDetectionAsync(config);

        Assert.Null(harness.Session.Processes.EventSource);
        Assert.False(harness.Session.Processes.IsUsingEvents);
    }

    // Asked for back to back and awaited by nobody, which is what the window does: a save pressed
    // while the driver is still opening has to land on the engine that opens, and a stop asked for
    // after both is the last word.
    [Fact]
    public async Task Work_runs_in_the_order_it_was_asked_for()
    {
        await using var harness = new SessionHarness();
        AppConfig config = Config();

        Task start = harness.Session.StartAsync(config);
        Task apply = harness.Session.ApplyAsync(config);
        Task stop = harness.Session.StopAsync();
        await Task.WhenAll(start, apply, stop);

        // Only a running engine resets the connections that escaped it, so the apply met one.
        Assert.Equal(1, harness.Redirector.ResetEscapedFlowsCalls);
        Assert.False(harness.Session.IsRunning);
    }

    // A11: the apply used to swallow its own failure, so a process launched frozen for the sake of a
    // new filter was resumed with the filter nowhere — the very leak launching it frozen exists to
    // close. Whoever waits on an apply must be able to tell it did not happen.
    [Fact]
    public async Task A_save_that_could_not_be_written_is_reported_rather_than_swallowed()
    {
        // A folder where the file should be: the write fails the way a locked or read-only file does.
        string blocked = Path.Combine(Path.GetTempPath(), $"proxydivert-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(blocked);
        try
        {
            await using var harness = new SessionHarness(new ConfigStore(blocked));

            await Assert.ThrowsAsync<IOException>(() => harness.Session.ApplyAsync(Config()));

            // And the queue behind it is not broken by it.
            await harness.Session.StartAsync(Config());
            Assert.True(harness.Session.IsRunning);
        }
        finally
        {
            Directory.Delete(blocked, recursive: true);
        }
    }

    [Fact]
    public async Task Disposing_the_session_switches_redirection_off_and_refuses_another_start()
    {
        await using var harness = new SessionHarness();
        await harness.Session.StartAsync(Config());

        await harness.Session.DisposeAsync();

        Assert.False(harness.Session.IsRunning);
        // A start queued behind the disposal would open a driver nobody is left to close.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Session.StartAsync(Config()));
    }
}
