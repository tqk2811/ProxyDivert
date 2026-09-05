using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using TqkLibrary.WinDivert.ProcessControl.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Saving a rule used to freeze the window for five to ten seconds, because re-testing the rules
// asked WMI for the command line of every redirected process, one query each, on the UI thread.
// What these assert is therefore a count of queries per ApplyRules — the thing that turns into
// seconds — not which processes came back.
//
// Start() is never called: it puts up WMI hooks against the real machine. ApplyRules is the whole
// path under test and it stands on its own.
public class ProcessWatcherCommandLineTests
{
    // Well above any pid Windows is handing out, so nothing here can collide with the test runner
    // itself — the watcher refuses to attach to its own process, which would silently skew a count.
    private const uint FirstFakePid = 900_000;

    private static ProcessRule Rule(string name, params ProcessCondition[] conditions) => new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = name,
        Condition = new ConditionGroup { Operator = ConditionOperator.All, Children = conditions.ToList() },
        PolicyIds = { Guid.NewGuid() },
    };

    private static ProcessNameCondition Process(string pattern)
        => new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = pattern };

    private static CommandLineCondition Arguments(string pattern)
        => new CommandLineCondition { Matcher = ArgumentMatcherType.Contains, Pattern = pattern };

    // A machine with `matching` copies of java.exe running the game, and the rest ordinary noise.
    private static (FakeProcessFinder Finder, FakeCommandLineReader Reader) Machine(int total, int matching)
    {
        var finder = new FakeProcessFinder();
        var reader = new FakeCommandLineReader();

        for (int i = 0; i < total; i++)
        {
            uint pid = FirstFakePid + (uint)i;
            bool isGame = i < matching;
            finder.Processes.Add(new ProcessInfo(pid, isGame ? "java.exe" : $"noise{i}.exe", null));
            reader.Lines[pid] = isGame ? "java.exe -jar minecraft.jar" : $"noise{i}.exe";
        }

        return (finder, reader);
    }

    private static ProcessWatcher Watcher(FakeProcessFinder finder, FakeCommandLineReader reader)
        => new ProcessWatcher(NullLogger<ProcessWatcher>.Instance, finder, reader);

    // The regression itself. Before the command line table this cost one query per redirected
    // process — thirty of them, at roughly 210ms each.
    [Fact]
    public void Applying_a_rule_that_asks_about_arguments_costs_one_command_line_sweep()
    {
        (FakeProcessFinder finder, FakeCommandLineReader reader) = Machine(total: 200, matching: 30);
        using ProcessWatcher watcher = Watcher(finder, reader);

        watcher.ApplyRules(new[] { Rule("game", Process("java.exe"), Arguments("minecraft")) });

        Assert.Equal(30, watcher.Tracked.Count);
        Assert.Equal(1, reader.SweepCount);
        Assert.Empty(reader.SingleReads);
    }

    [Fact]
    public void Applying_the_same_rules_again_costs_no_command_line_query_at_all()
    {
        (FakeProcessFinder finder, FakeCommandLineReader reader) = Machine(total: 200, matching: 30);
        using ProcessWatcher watcher = Watcher(finder, reader);
        IReadOnlyList<ProcessRule> rules = new[] { Rule("game", Process("java.exe"), Arguments("minecraft")) };

        watcher.ApplyRules(rules);
        watcher.ApplyRules(rules);

        Assert.Equal(1, reader.SweepCount);
        Assert.Empty(reader.SingleReads);
    }

    [Fact]
    public void Rules_that_do_not_ask_about_arguments_never_touch_the_command_line_reader()
    {
        (FakeProcessFinder finder, FakeCommandLineReader reader) = Machine(total: 200, matching: 30);
        using ProcessWatcher watcher = Watcher(finder, reader);

        watcher.ApplyRules(new[] { Rule("by name", Process("java.exe")) });

        Assert.Equal(30, watcher.Tracked.Count);
        Assert.Equal(0, reader.SweepCount);
        Assert.Empty(reader.SingleReads);
    }

    // The table is filled lazily, so the moment a rule first asks about arguments it has to catch
    // the processes that were already running — not only the ones that start afterwards.
    [Fact]
    public void A_rule_that_starts_asking_about_arguments_catches_the_processes_already_running()
    {
        (FakeProcessFinder finder, FakeCommandLineReader reader) = Machine(total: 50, matching: 5);
        using ProcessWatcher watcher = Watcher(finder, reader);

        watcher.ApplyRules(new[] { Rule("by name", Process("notepad.exe")) });
        Assert.Empty(watcher.Tracked);

        watcher.ApplyRules(new[] { Rule("game", Process("java.exe"), Arguments("minecraft")) });

        Assert.Equal(5, watcher.Tracked.Count);
        Assert.Equal(1, reader.SweepCount);
    }

    [Fact]
    public void A_process_that_stops_matching_after_an_argument_edit_is_dropped()
    {
        (FakeProcessFinder finder, FakeCommandLineReader reader) = Machine(total: 50, matching: 5);
        using ProcessWatcher watcher = Watcher(finder, reader);
        var detached = new List<TrackedProcess>();
        watcher.ProcessDetached += detached.Add;

        watcher.ApplyRules(new[] { Rule("game", Process("java.exe"), Arguments("minecraft")) });
        Assert.Equal(5, watcher.Tracked.Count);

        watcher.ApplyRules(new[] { Rule("game", Process("java.exe"), Arguments("terraria")) });

        Assert.Empty(watcher.Tracked);
        Assert.Equal(5, detached.Count);
    }

    private sealed class FakeProcessFinder : IProcessFinder
    {
        public List<ProcessInfo> Processes { get; } = new List<ProcessInfo>();

        public IReadOnlyList<ProcessInfo> ListAll() => Processes;

        public IReadOnlyList<ProcessInfo> FindByName(string name)
            => Processes.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        public ProcessInfo? FindById(uint pid) => Processes.FirstOrDefault(p => p.Id == pid);
    }

    private sealed class FakeCommandLineReader : IProcessCommandLineReader
    {
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
