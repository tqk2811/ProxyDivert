using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The process table is what everything else now reads instead of asking the operating system, so
// these check the three things it promises: it holds all four facts about every process, it says
// so exactly once when a process appears or goes, and it never asks about the same process twice.
//
// Refresh() is used rather than Start(): the reconcile is the whole mechanism, and Start would also
// hook real WMI and spawn a loop, neither of which belongs in a test.
public class ProcessInventoryTests
{
    private static ProcessInventory Inventory(FakeProcessMachine machine)
        => new ProcessInventory(NullLogger<ProcessInventory>.Instance, machine, machine);

    private sealed class Recorder
    {
        public List<ProcessSnapshot> Started { get; } = new List<ProcessSnapshot>();
        public List<ProcessSnapshot> Stopped { get; } = new List<ProcessSnapshot>();

        public Recorder(ProcessInventory inventory)
        {
            inventory.ProcessStarted += p => Started.Add(p);
            inventory.ProcessStopped += p => Stopped.Add(p);
        }
    }

    [Fact]
    public void A_first_pass_holds_every_process_with_all_four_facts()
    {
        var machine = new FakeProcessMachine()
            .Start(100, "chrome.exe", @"C:\Chrome\chrome.exe", "chrome --type=renderer", parentPid: 8);

        using ProcessInventory inventory = Inventory(machine);
        inventory.Refresh();

        ProcessSnapshot? chrome = inventory.Get(100);
        Assert.NotNull(chrome);
        Assert.Equal(100u, chrome!.ProcessId);
        Assert.Equal(@"C:\Chrome\chrome.exe", chrome.ExecutablePath);
        Assert.Equal("chrome --type=renderer", chrome.CommandLine);
        Assert.Equal(8u, chrome.ParentProcessId);
    }

    [Fact]
    public void A_process_is_announced_once_however_often_the_table_is_rebuilt()
    {
        var machine = new FakeProcessMachine().Start(100, "chrome.exe");
        using ProcessInventory inventory = Inventory(machine);
        var events = new Recorder(inventory);

        inventory.Refresh();
        inventory.Refresh();
        inventory.Refresh();

        Assert.Single(events.Started);
        Assert.Empty(events.Stopped);
    }

    [Fact]
    public void A_command_line_is_read_once_and_then_remembered()
    {
        var machine = new FakeProcessMachine().Start(100, "chrome.exe").Start(200, "code.exe");
        using ProcessInventory inventory = Inventory(machine);

        inventory.Refresh();
        inventory.Refresh();
        inventory.Refresh();

        // Twice would mean the table forgot — the exact cost this whole design exists to avoid.
        Assert.Equal(1, machine.DetailReads[100]);
        Assert.Equal(1, machine.DetailReads[200]);
    }

    [Fact]
    public void A_process_that_starts_later_is_announced_when_it_appears()
    {
        var machine = new FakeProcessMachine().Start(100, "chrome.exe");
        using ProcessInventory inventory = Inventory(machine);
        var events = new Recorder(inventory);
        inventory.Refresh();

        machine.Start(200, "notepad.exe");
        inventory.Refresh();

        Assert.Equal(new uint[] { 100, 200 }, events.Started.Select(p => p.ProcessId));
        Assert.Equal(2, inventory.Count);
    }

    [Fact]
    public void A_process_that_exits_is_announced_and_dropped()
    {
        // Two, because an empty listing is read as a failed call rather than as an empty machine.
        var machine = new FakeProcessMachine().Start(100, "chrome.exe").Start(4, "System");
        using ProcessInventory inventory = Inventory(machine);
        var events = new Recorder(inventory);
        inventory.Refresh();

        machine.Exit(100);
        inventory.Refresh();

        ProcessSnapshot gone = Assert.Single(events.Stopped);
        Assert.Equal("chrome.exe", gone.Name);
        Assert.Null(inventory.Get(100));
        Assert.Equal(1, inventory.Count);
    }

    [Fact]
    public void A_pid_handed_to_another_program_ends_one_process_and_starts_another()
    {
        var start = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var machine = new FakeProcessMachine().Start(100, "chrome.exe", startedUtc: start);

        using ProcessInventory inventory = Inventory(machine);
        var events = new Recorder(inventory);
        inventory.Refresh();

        // Same pid, a different program, and no stop event ever arrived.
        machine.Exit(100).Start(100, "malware.exe", startedUtc: start.AddMinutes(1));
        inventory.Refresh();

        Assert.Equal(new[] { "chrome.exe", "malware.exe" }, events.Started.Select(p => p.Name));
        Assert.Equal(new[] { "chrome.exe" }, events.Stopped.Select(p => p.Name));
        Assert.Equal("malware.exe", inventory.Get(100)!.Name);
    }

    [Fact]
    public void A_process_whose_details_will_not_read_is_tried_again_next_time()
    {
        var machine = new FakeProcessMachine().Start(100, "protected.exe");
        machine.Unreadable.Add(100);

        using ProcessInventory inventory = Inventory(machine);
        inventory.Refresh();

        Assert.Null(inventory.Get(100)!.CommandLine);
        Assert.False(inventory.Get(100)!.DetailsRead);

        // A process is often younger than the event announcing it, so a refusal is not an answer.
        machine.Unreadable.Remove(100);
        inventory.Refresh();

        Assert.Equal(@"""C:\Programs\protected.exe""", inventory.Get(100)!.CommandLine);
        Assert.True(inventory.Get(100)!.DetailsRead);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_the_others_or_the_table()
    {
        var machine = new FakeProcessMachine().Start(100, "chrome.exe");
        using ProcessInventory inventory = Inventory(machine);

        var seen = new List<uint>();
        inventory.ProcessStarted += _ => throw new InvalidOperationException("a broken handler");
        inventory.ProcessStarted += p => seen.Add(p.ProcessId);

        inventory.Refresh();

        Assert.Equal(new uint[] { 100 }, seen);
        Assert.Equal(1, inventory.Count);
    }

    [Fact]
    public void A_listing_that_comes_back_empty_leaves_the_table_alone()
    {
        var machine = new FakeProcessMachine().Start(100, "chrome.exe").Start(200, "code.exe");
        using ProcessInventory inventory = Inventory(machine);
        var events = new Recorder(inventory);
        inventory.Refresh();

        // A machine with no processes at all is not a state this code can be running in, so an empty
        // listing means the call failed. Emptying the table on it would detach every redirected
        // process until the next pass — far worse than carrying a stale entry for one interval.
        machine.Exit(100).Exit(200);
        inventory.Refresh();

        Assert.Empty(events.Stopped);
        Assert.Equal(2, inventory.Count);
    }
}
