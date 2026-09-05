using System;
using ProxyDivert.Core.Processes;
using Xunit;

namespace ProxyDivert.Core.Tests;

// WMI hands the events of one watcher over strictly in turn, so a handler that spends ~210ms on a
// WMI query holds up every process behind it. The way out is to notice the queue backing up and
// read the whole machine once — and the way to notice is the age of the event being handled, which
// during a burst rises in exact step with what the handler costs.
//
// These run on a fake clock: nothing here should depend on how fast the test machine is.
public class ProcessEventBacklogTests
{
    private sealed class FakeClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        public long MonotonicMs { get; set; } = 1_000_000;

        public void Advance(TimeSpan by)
        {
            UtcNow += by;
            MonotonicMs += (long)by.TotalMilliseconds;
        }
    }

    private static ProcessEventBacklog Backlog(FakeClock clock, int thresholdMs = 500)
        => new ProcessEventBacklog(thresholdMs, () => clock.UtcNow, () => clock.MonotonicMs);

    // What WMI puts on the event: a FILETIME, in UTC.
    private static object EventCreated(FakeClock clock, TimeSpan ago)
        => (clock.UtcNow - ago).ToFileTimeUtc();

    [Fact]
    public void An_event_handled_as_soon_as_it_happens_is_not_a_backlog()
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock);

        Assert.False(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromMilliseconds(5))));
    }

    [Fact]
    public void An_event_older_than_the_threshold_asks_for_a_catch_up()
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock);

        Assert.True(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromMilliseconds(800))));
    }

    // A catch-up reads every process on the machine. Doing that again for the next event of the
    // same burst would be its own kind of waste.
    [Fact]
    public void A_second_stale_event_moments_later_does_not_ask_for_another_catch_up()
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock);

        Assert.True(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromSeconds(3))));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public void A_burst_that_keeps_going_is_caught_up_with_again_a_second_later()
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock);
        Assert.True(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromSeconds(3))));

        clock.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.True(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromSeconds(3))));
    }

    // An unknown age is not evidence of anything. The watcher then behaves exactly as it did
    // before this existed: one query per process.
    [Theory]
    [InlineData(null)]
    [InlineData("not a time")]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void An_event_whose_age_cannot_be_read_never_asks_for_a_catch_up(object? rawTimeCreated)
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock);

        Assert.False(backlog.ShouldCatchUp(rawTimeCreated));
    }

    [Fact]
    public void A_threshold_of_zero_turns_catching_up_off()
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock, thresholdMs: 0);

        Assert.False(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void The_threshold_can_be_changed_while_running()
    {
        var clock = new FakeClock();
        ProcessEventBacklog backlog = Backlog(clock, thresholdMs: 5_000);
        Assert.False(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromSeconds(1))));

        backlog.ThresholdMs = 500;

        Assert.True(backlog.ShouldCatchUp(EventCreated(clock, TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void The_age_of_an_event_is_read_from_its_file_time()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(ProcessEventBacklog.TryGetEventAge(
            now.AddSeconds(-7).ToFileTimeUtc(), now, out TimeSpan age));

        Assert.Equal(7, age.TotalSeconds, precision: 3);
    }

    // A clock adjusted backwards would otherwise make an event look as though it happens later than
    // it is handled, and a negative age compares as "not behind" only by accident.
    [Fact]
    public void An_event_stamped_in_the_future_reads_as_no_age_at_all()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(ProcessEventBacklog.TryGetEventAge(
            now.AddMinutes(5).ToFileTimeUtc(), now, out TimeSpan age));

        Assert.Equal(TimeSpan.Zero, age);
    }
}
