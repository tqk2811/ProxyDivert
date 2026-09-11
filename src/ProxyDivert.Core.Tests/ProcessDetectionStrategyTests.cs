using System;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Engine.Extensions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using TqkLibrary.WinDivert.Redirect;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A detection mode is three settings in three objects — what the process table follows, whether the
// tracker attaches on a start event, and whether the redirector asks about every pid it meets. These
// hold each mode to all three, so a mode can no longer be half of one and half of the other.
public class ProcessDetectionStrategyTests
{
    public static TheoryData<ProcessDetectionMode> Modes()
    {
        var modes = new TheoryData<ProcessDetectionMode>();
        foreach (ProcessDetectionMode mode in Enum.GetValues<ProcessDetectionMode>()) modes.Add(mode);
        return modes;
    }

    // A value the settings page offers with nothing behind it would only show up as Start throwing.
    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_mode_the_settings_offer_has_a_strategy_that_is_that_mode(ProcessDetectionMode mode)
        => Assert.Equal(mode, mode.Strategy().Mode);

    [Fact]
    public void A_mode_the_file_cannot_mean_is_refused_by_name()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ((ProcessDetectionMode)42).Strategy());

    [Theory]
    [InlineData(ProcessEventSourceKind.Etw)]
    [InlineData(ProcessEventSourceKind.Wmi)]
    public void Process_events_follow_the_source_the_user_picked(ProcessEventSourceKind source)
    {
        using ProcessInventory inventory = Inventory(new FakeProcessMachine());

        ProcessDetectionMode.ProcessEvents.Strategy().ConfigureInventory(inventory, source);

        Assert.Equal(source, inventory.EventSource);
    }

    // The source is still remembered in the file for when the user switches back; sniffing just
    // does not listen to it.
    [Theory]
    [InlineData(ProcessEventSourceKind.Etw)]
    [InlineData(ProcessEventSourceKind.Wmi)]
    public void Sniffing_follows_no_process_events_whatever_source_is_picked(ProcessEventSourceKind source)
    {
        using ProcessInventory inventory = Inventory(new FakeProcessMachine());

        ProcessDetectionMode.NetworkSniff.Strategy().ConfigureInventory(inventory, source);

        Assert.Null(inventory.EventSource);
    }

    [Fact]
    public void Sniffing_hands_the_redirector_the_engines_judge()
    {
        var options = new RedirectOptions();
        Func<uint, bool?> judge = _ => true;

        ProcessDetectionMode.NetworkSniff.Strategy().ConfigureRedirect(options, judge);

        Assert.Same(judge, options.ShouldTrackProcess);
    }

    // A judge on the options is what switches the redirector to one handle for the whole machine,
    // so this mode clears one rather than trusting nobody set it.
    [Fact]
    public void Process_events_leave_the_redirector_to_the_pids_it_is_handed()
    {
        var options = new RedirectOptions { ShouldTrackProcess = _ => true };

        ProcessDetectionMode.ProcessEvents.Strategy().ConfigureRedirect(options, _ => true);

        Assert.Null(options.ShouldTrackProcess);
    }

    [Theory]
    [InlineData(ProcessDetectionMode.ProcessEvents, true)]
    [InlineData(ProcessDetectionMode.NetworkSniff, false)]
    public void Only_process_events_attach_a_process_the_moment_it_starts(ProcessDetectionMode mode, bool attached)
    {
        var machine = new FakeProcessMachine();
        using ProcessInventory inventory = Inventory(machine);
        using var tracker = new ProcessRuleTracker(NullLogger<ProcessRuleTracker>.Instance, inventory);
        mode.Strategy().StartTracker(tracker, new[] { Filter("chrome.exe") });

        machine.Start(100, "chrome.exe");
        inventory.Refresh();

        Assert.Equal(attached, tracker.IsTracked(100));
    }

    // Whichever way new processes are found, a browser that was open before the switch was flipped
    // is claimed at once — waiting for it to open a new socket would leave its old ones unredirected.
    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_mode_claims_what_was_already_running(ProcessDetectionMode mode)
    {
        var machine = new FakeProcessMachine().Start(100, "chrome.exe");
        using ProcessInventory inventory = Inventory(machine);
        inventory.Refresh();
        using var tracker = new ProcessRuleTracker(NullLogger<ProcessRuleTracker>.Instance, inventory);

        mode.Strategy().StartTracker(tracker, new[] { Filter("chrome.exe") });

        Assert.True(tracker.IsTracked(100));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_mode_lets_go_of_a_process_that_exits(ProcessDetectionMode mode)
    {
        // Something else stays running: a listing that comes back empty is read as the listing
        // having failed, and retires nothing.
        var machine = new FakeProcessMachine().Start(100, "chrome.exe").Start(4, "System");
        using ProcessInventory inventory = Inventory(machine);
        inventory.Refresh();
        using var tracker = new ProcessRuleTracker(NullLogger<ProcessRuleTracker>.Instance, inventory);
        mode.Strategy().StartTracker(tracker, new[] { Filter("chrome.exe") });

        machine.Exit(100);
        inventory.Refresh();

        Assert.False(tracker.IsTracked(100));
    }

    private static ProcessInventory Inventory(FakeProcessMachine machine)
        => new ProcessInventory(NullLogger<ProcessInventory>.Instance, machine, machine);

    private static ProcessRule Filter(string pattern)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = pattern,
            Condition = new ConditionGroup
            {
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = pattern },
                },
            },
            PolicyIds = { Guid.NewGuid() },
        };
}
