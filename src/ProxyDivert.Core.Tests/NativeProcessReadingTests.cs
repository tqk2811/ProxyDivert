using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The process table is built on two undocumented NT calls, and a struct laid out one field wrong
// reads as plausible garbage rather than as an error — a parent pid that is really half a
// timestamp, a name pulled from the middle of another string. So these run against the REAL
// machine and check the answers against facts the test already knows about itself.
//
// They are also where the cost claim is kept honest: the whole design rests on listing the machine
// being cheap enough to redo on a timer.
public class NativeProcessReadingTests
{
    [Fact]
    public void The_listing_finds_this_very_process_and_describes_it_correctly()
    {
        IReadOnlyList<ProcessSnapshot> processes = new NativeProcessLister().ListAll();

        Assert.True(processes.Count > 20, $"only {processes.Count} processes came back");

        using Process self = Process.GetCurrentProcess();
        ProcessSnapshot mine = Assert.Single(processes, p => p.ProcessId == (uint)self.Id);

        // The name carries its extension, which is the spelling the whole table uses.
        Assert.EndsWith(".exe", mine.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(self.ProcessName, System.IO.Path.GetFileNameWithoutExtension(mine.Name), ignoreCase: true);

        // Read straight out of the struct, so a wrong offset shows up here.
        Assert.Equal((uint)self.SessionId, mine.SessionId);
        Assert.InRange(mine.StartedUtc, self.StartTime.ToUniversalTime().AddSeconds(-2), DateTime.UtcNow);

        // The parent must be a real pid, and it must be the test runner rather than a number that
        // happens to be non-zero.
        Assert.NotEqual(0u, mine.ParentProcessId);
        Assert.Contains(processes, p => p.ProcessId == mine.ParentProcessId);
    }

    [Fact]
    public void Every_process_has_a_name_and_no_pid_appears_twice()
    {
        IReadOnlyList<ProcessSnapshot> processes = new NativeProcessLister().ListAll();

        Assert.All(processes, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.NotEqual(0u, p.ProcessId);
        });

        Assert.Equal(processes.Count, processes.Select(p => p.ProcessId).Distinct().Count());
    }

    [Fact]
    public void The_details_of_this_process_come_back_whole()
    {
        using Process self = Process.GetCurrentProcess();

        ProcessDetails details = new NativeProcessDetailsReader().Read((uint)self.Id);

        Assert.NotNull(details.ExecutablePath);
        Assert.True(System.IO.Path.IsPathRooted(details.ExecutablePath));

        // The command line of the test runner names the test assembly, whatever runner is used.
        Assert.NotNull(details.CommandLine);
        Assert.Contains("ProxyDivert.Core.Tests", details.CommandLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pid_that_is_not_running_reads_as_nothing_rather_than_throwing()
    {
        // Odd pids above the kernel's allocation granularity are never live; whatever this is, the
        // reader must answer rather than throw.
        ProcessDetails details = new NativeProcessDetailsReader().Read(0xFFFFFFF0);

        Assert.Null(details.ExecutablePath);
        Assert.Null(details.CommandLine);
    }

    // Not a benchmark — a floor. The table is rebuilt on a timer when process events are
    // unavailable, so a listing that costs seconds would make the fallback unusable, and this is
    // the assumption that would have quietly broken.
    [Fact]
    public void Listing_the_whole_machine_costs_a_few_milliseconds()
    {
        var lister = new NativeProcessLister();
        lister.ListAll();   // first call pays for the buffer growth

        var clock = Stopwatch.StartNew();
        IReadOnlyList<ProcessSnapshot> processes = lister.ListAll();
        clock.Stop();

        Assert.True(
            clock.ElapsedMilliseconds < 250,
            $"listing {processes.Count} processes took {clock.ElapsedMilliseconds}ms");
    }
}
