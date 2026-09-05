using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using TqkLibrary.WinDivert.ProcessControl.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A WMI query for one process costs about as much as a query for all of them — roughly 210ms
// against 230ms on a machine running 448 processes. So what matters here is not which command line
// comes back but HOW MANY queries it took: that is the number that turned into the five to ten
// seconds the window used to freeze for when a rule was saved.
public class ProcessCommandLineCacheTests
{
    // No fake session lookup would make these tests clearer, so they pretend nothing is a service
    // and let the reader answer for everything. The service shortcut has a test of its own below.
    private static ProcessCommandLineCache Cache(
        FakeCommandLineReader reader, int maxEntries = 4096, bool everythingIsAService = false)
        => new ProcessCommandLineCache(
            reader, NullLogger.Instance, _ => everythingIsAService, maxEntries);

    private static IReadOnlyList<ProcessInfo> Running(params (uint Id, string Name)[] processes)
    {
        var list = new List<ProcessInfo>();
        foreach ((uint id, string name) in processes) list.Add(new ProcessInfo(id, name, null));
        return list;
    }

    private static IReadOnlyList<ProcessInfo> ManyRunning(uint count, uint firstId = 1000)
    {
        var list = new List<ProcessInfo>();
        for (uint i = 0; i < count; i++) list.Add(new ProcessInfo(firstId + i, $"p{i}.exe", null));
        return list;
    }

    [Fact]
    public void A_command_line_is_read_once_and_answered_from_memory_afterwards()
    {
        var reader = new FakeCommandLineReader { Lines = { [1000] = "java.exe -jar minecraft" } };
        ProcessCommandLineCache cache = Cache(reader);

        Assert.Equal("java.exe -jar minecraft", cache.Get(1000, "java.exe"));
        Assert.Equal("java.exe -jar minecraft", cache.Get(1000, "java.exe"));

        Assert.Single(reader.SingleReads);
    }

    // The one that pays for the whole class: about half the processes on a machine are services
    // this tool may not open, and without recording "asked, cannot read" they would be asked about
    // again on every single pass.
    [Fact]
    public void A_process_whose_command_line_cannot_be_read_is_never_asked_about_again()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);

        Assert.Null(cache.Get(1000, "svchost.exe"));
        Assert.Null(cache.Get(1000, "svchost.exe"));

        Assert.Single(reader.SingleReads);
    }

    [Fact]
    public void A_machine_wide_sweep_marks_every_process_it_left_out_as_unreadable()
    {
        var reader = new FakeCommandLineReader { Lines = { [1000] = "chrome.exe --type=renderer" } };
        ProcessCommandLineCache cache = Cache(reader);

        cache.EnsureLoaded(Running((1000, "chrome.exe"), (1001, "svchost.exe"), (1002, "csrss.exe")));

        Assert.Null(cache.Get(1001, "svchost.exe"));
        Assert.Null(cache.Get(1002, "csrss.exe"));
        Assert.Empty(reader.SingleReads);
    }

    [Fact]
    public void Filling_many_gaps_costs_one_machine_wide_query_instead_of_one_each()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);

        cache.EnsureLoaded(ManyRunning(10));

        Assert.Equal(1, reader.SweepCount);
        Assert.Empty(reader.SingleReads);
    }

    [Fact]
    public void Filling_a_single_gap_asks_about_that_process_only()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);

        cache.EnsureLoaded(ManyRunning(1));

        Assert.Equal(0, reader.SweepCount);
        Assert.Single(reader.SingleReads);
    }

    [Fact]
    public void A_second_pass_over_the_same_processes_asks_nothing()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);
        IReadOnlyList<ProcessInfo> processes = ManyRunning(10);

        cache.EnsureLoaded(processes);
        cache.EnsureLoaded(processes);

        Assert.Equal(1, reader.SweepCount);
        Assert.Empty(reader.SingleReads);
    }

    [Fact]
    public void A_process_that_has_exited_is_forgotten()
    {
        var reader = new FakeCommandLineReader { Lines = { [1000] = "notepad.exe a.txt" } };
        ProcessCommandLineCache cache = Cache(reader);

        cache.Get(1000, "notepad.exe");
        cache.Forget(1000);
        cache.Get(1000, "notepad.exe");

        Assert.Equal(2, reader.SingleReads.Count);
    }

    // Windows hands pids out again, sometimes within seconds. Serving the previous owner's command
    // line would route the wrong program — or fail to route the right one.
    [Fact]
    public void A_pid_handed_to_another_program_is_not_answered_with_the_old_command_line()
    {
        var reader = new FakeCommandLineReader { Lines = { [1000] = "java.exe -jar minecraft" } };
        ProcessCommandLineCache cache = Cache(reader);

        Assert.Equal("java.exe -jar minecraft", cache.Get(1000, "java.exe"));

        reader.Lines[1000] = "notepad.exe";
        Assert.Equal("notepad.exe", cache.Get(1000, "notepad.exe"));
    }

    [Fact]
    public void Processes_that_are_no_longer_running_are_dropped()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);
        cache.EnsureLoaded(Running((1000, "a.exe"), (1001, "b.exe"), (1002, "c.exe")));

        cache.Retain(Running((1000, "a.exe")));

        Assert.Equal(1, cache.Count);
        cache.Get(1001, "b.exe");
        Assert.Single(reader.SingleReads);
    }

    // The safety net for a stop event that never arrived: the scan sees the pid again under a
    // different name and drops what was remembered about it.
    [Fact]
    public void A_pid_reused_between_two_scans_is_dropped_by_the_next_scan()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);
        cache.EnsureLoaded(Running((1000, "a.exe"), (1001, "b.exe"), (1002, "c.exe")));

        cache.Retain(Running((1000, "somethingelse.exe"), (1001, "b.exe"), (1002, "c.exe")));

        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void A_process_started_after_the_sweep_is_read_on_its_own()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader);
        cache.EnsureLoaded(ManyRunning(10));

        cache.Get(9999, "new.exe");

        Assert.Equal(1, reader.SweepCount);
        Assert.Single(reader.SingleReads);
    }

    // A service runs under an account this tool cannot open, so the query is known in advance to
    // come back empty. Skipping it is the difference between paying 210ms per new svchost and not.
    [Fact]
    public void A_service_process_is_recorded_as_unreadable_without_asking_wmi()
    {
        var reader = new FakeCommandLineReader { Lines = { [1000] = "svchost.exe -k netsvcs" } };
        ProcessCommandLineCache cache = Cache(reader, everythingIsAService: true);

        Assert.Null(cache.Get(1000, "svchost.exe"));

        Assert.Empty(reader.SingleReads);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void The_table_is_emptied_when_it_grows_past_its_limit()
    {
        var reader = new FakeCommandLineReader();
        ProcessCommandLineCache cache = Cache(reader, maxEntries: 4);

        cache.EnsureLoaded(ManyRunning(5));

        Assert.Equal(0, cache.Count);
    }

    // Stands in for WMI: it answers from a table and, more importantly, counts how often it was
    // asked and in which shape.
    private sealed class FakeCommandLineReader : IProcessCommandLineReader
    {
        // A process absent from here is one whose command line cannot be read, exactly as
        // ReadAll behaves against the real Win32_Process.
        public Dictionary<uint, string> Lines { get; } = new Dictionary<uint, string>();

        public List<uint> SingleReads { get; } = new List<uint>();

        public int SweepCount { get; private set; }

        public string? Read(uint processId)
        {
            SingleReads.Add(processId);
            return Lines.TryGetValue(processId, out string? line) ? line : null;
        }

        public IReadOnlyDictionary<uint, string> ReadAll()
        {
            SweepCount++;
            return new Dictionary<uint, string>(Lines);
        }
    }
}
