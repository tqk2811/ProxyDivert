using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Helpers;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.ViewModels.Conditions;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.ProcessControl.Models;

namespace ProxyDivert.Wpf.ViewModels;

/// <summary>The filter window: a name, a tree of conditions, and what to do with what matches.</summary>
/// <remarks>
/// It edits a copy. The tree is cloned on the way in and only written back in
/// <see cref="ApplyTo"/>, so closing the window without saving leaves the filter exactly as it was
/// — including for a filter the engine is running against right now.
/// </remarks>
public sealed partial class ProcessFilterViewModel : ObservableObject
{
    // True while the test run is writing its answers onto the rows, so the tree-changed handler
    // does not immediately wipe what the test just put there.
    private bool _testing;

    public ProcessFilterViewModel(ProcessRule rule, IEnumerable<RoutingPolicy> policies)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));

        _name = rule.Name;
        _includeChildren = rule.IncludeChildren;

        foreach (RoutingPolicy policy in policies) Policies.Add(policy);
        _selectedPolicy = Policies.FirstOrDefault(p => p.Id == rule.PolicyId) ?? Policies.FirstOrDefault();

        Root = new ConditionGroupViewModel(RootGroupOf(rule));
        Root.Changed += OnTreeChanged;
        _summary = ConditionTextBuilder.Describe(Root.ToModel());

        foreach (ProcessInfo process in ListRunningProcesses()) TestProcesses.Add(new RunningProcess(process));
    }

    /// <summary>The outermost group. Everything the editor shows hangs off this.</summary>
    public ConditionGroupViewModel Root { get; }

    public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

    public ObservableCollection<RunningProcess> TestProcesses { get; } = new ObservableCollection<RunningProcess>();

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _includeChildren;

    [ObservableProperty]
    private RoutingPolicy? _selectedPolicy;

    /// <summary>The whole filter read back as one sentence. Rebuilt on every edit.</summary>
    [ObservableProperty]
    private string _summary;

    [ObservableProperty]
    private RunningProcess? _selectedTestProcess;

    /// <summary>What the last try came out as, for the whole filter; null before anything was tried.</summary>
    [ObservableProperty]
    private ConditionResult? _testResult;

    /// <summary>Writes the edited filter back onto the rule. Called only when the user saves.</summary>
    public void ApplyTo(ProcessRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));

        rule.Name = string.IsNullOrWhiteSpace(Name) ? DefaultName() : Name.Trim();
        rule.Condition = Root.ToModel();
        rule.IncludeChildren = IncludeChildren;
        if (SelectedPolicy != null) rule.PolicyId = SelectedPolicy.Id;
    }

    /// <summary>
    /// Runs the filter against one process that is running right now and colours every row with
    /// its own answer.
    /// </summary>
    /// <remarks>
    /// The point is not the yes/no at the bottom — it is seeing WHICH row said no. Without it the
    /// only way to find out why a filter does not catch a program is to save it, start the engine,
    /// and stare at the redirected list.
    /// </remarks>
    [RelayCommand]
    private void RunTest()
    {
        RunningProcess? process = SelectedTestProcess;
        if (process is null) return;

        _testing = true;
        try
        {
            // Read on demand, for this one process: the same WMI query the watcher pays for, and
            // there is no reason to pay it for every process in the drop-down.
            string? commandLine = new ProcessCommandLineReader(NullLogger.Instance).Read(process.Id);
            Evaluate(Root, process.Name, process.Path, commandLine);
            TestResult = Root.TestResult;
        }
        finally
        {
            _testing = false;
        }
    }

    private static void Evaluate(ConditionNodeViewModel node, string name, string? path, string? commandLine)
    {
        node.TestResult = ProcessRuleMatcher.Evaluate(node.ToModel(), name, path, commandLine);

        if (node is ConditionGroupViewModel group)
            foreach (ConditionNodeViewModel child in group.Children)
                Evaluate(child, name, path, commandLine);
    }

    private void OnTreeChanged()
    {
        Summary = ConditionTextBuilder.Describe(Root.ToModel());

        // An edit after a test makes the colours a lie about the tree as it is now, so they go.
        if (_testing || TestResult is null) return;

        _testing = true;
        try
        {
            TestResult = null;
            Root.ClearTestResult();
        }
        finally
        {
            _testing = false;
        }
    }

    // A filter with no name is still a row in a list that has to say something. The first thing
    // the user typed is what they would have called it anyway.
    private string DefaultName()
    {
        string? pattern = FirstPattern(Root.ToModel());
        return string.IsNullOrWhiteSpace(pattern) ? Loc.S("Str.Process.UnnamedFilter") : pattern!.Trim();
    }

    private static string? FirstPattern(ProcessCondition? condition) => condition switch
    {
        LeafCondition leaf when !string.IsNullOrWhiteSpace(leaf.Pattern) => leaf.Pattern,
        ConditionGroup group => group.Children.Select(FirstPattern).FirstOrDefault(p => p != null),
        _ => null,
    };

    // A filter written by hand into the config file can have a single condition at its root, and
    // the editor only knows how to show groups. Wrapping it changes nothing about what it matches.
    private static ConditionGroup RootGroupOf(ProcessRule rule) => rule.Condition switch
    {
        ConditionGroup group => (ConditionGroup)group.Clone(),
        ProcessCondition condition => new ConditionGroup { Children = { condition.Clone() } },
        _ => ConditionGroup.CreateDefault(),
    };

    private static IReadOnlyList<ProcessInfo> ListRunningProcesses()
    {
        try
        {
            return new ProcessFinder().ListAll();
        }
        catch
        {
            // The picker is a convenience; a machine that will not enumerate is not a reason to
            // refuse to open the editor.
            return Array.Empty<ProcessInfo>();
        }
    }

    /// <summary>One entry in the "try it against" picker.</summary>
    public sealed class RunningProcess
    {
        public RunningProcess(ProcessInfo process)
        {
            Id = process.Id;
            Name = process.Name;
            Path = process.ExecutablePath;
        }

        public uint Id { get; }
        public string Name { get; }
        public string? Path { get; }

        public string Display => $"{Name} ({Id})";
    }
}
