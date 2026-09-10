using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.Bindings.Interfaces;
using ProxyDivert.Wpf.Services;
using ProxyDivert.Wpf.Views;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;

namespace ProxyDivert.Wpf.ViewModels;

// The Processes tab: which programs get redirected, and which ones the engine is actually holding
// right now — shown as a tree, because a process adopted through IncludeChildren only makes sense
// underneath the process that dragged it in.
//
// A filter is a name, a condition tree and an action, and only the name and the action are small
// enough to edit in a grid cell. The conditions are shown as the sentence they read as, and edited
// in a window of their own.
public sealed partial class ProcessesViewModel : ObservableObject, IDragList
{
    private readonly AppServices _services;

    public ObservableCollection<ProcessFilterRowViewModel> Rules { get; }
        = new ObservableCollection<ProcessFilterRowViewModel>();

    /// <summary>Roots of the redirected-process tree; children hang off <see cref="AppliedProcessNode.Children"/>.</summary>
    public ObservableCollection<AppliedProcessNode> AppliedProcesses { get; }
        = new ObservableCollection<AppliedProcessNode>();

    public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

    [ObservableProperty]
    private ProcessFilterRowViewModel? _selectedRule;

    [ObservableProperty]
    private AppliedProcessNode? _selectedProcess;

    // The column widths of the redirected-process tree. They live here rather than in the view
    // because a tree has no columns of its own: the headings and every row bind to these numbers,
    // which is what makes them line up, and dragging the splitter in the heading writes the new
    // width back here. Keeping them on the view model also means a tab switch does not throw the
    // arrangement away.
    [ObservableProperty]
    private double _appliedPidWidth = 64;

    [ObservableProperty]
    private double _appliedNameWidth = 190;

    [ObservableProperty]
    private double _appliedFilterWidth = 190;

    public ProcessesViewModel(AppServices services)
    {
        _services = services;
        Reload();
        RefreshApplied();
    }

    public void Reload()
    {
        // Held across the refill: this runs on every tab switch, and clearing the grid would
        // otherwise drop whatever row the user had picked before stepping away.
        Guid? previous = SelectedRule?.Id;

        Rules.Clear();
        foreach (ProcessRule rule in _services.Config.ProcessRules)
            Rules.Add(new ProcessFilterRowViewModel(rule));

        Policies.Clear();
        foreach (RoutingPolicy policy in _services.Config.Policies) Policies.Add(policy);

        SelectedRule = previous is null ? null : Rules.FirstOrDefault(r => r.Id == previous);
    }

    [RelayCommand]
    public void RefreshApplied()
    {
        AppliedProcesses.Clear();

        foreach (AppliedProcessNode root in BuildTree(_services.Engine.TrackedProcesses))
            AppliedProcesses.Add(root);
    }

    /// <summary>The redirected processes as the tab shows them: roots, with adopted children under them.</summary>
    internal static List<AppliedProcessNode> BuildTree(IEnumerable<TrackedProcess> processes)
    {
        List<TrackedProcess> tracked = processes
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();

        Dictionary<uint, TrackedProcess> byId = tracked
            .GroupBy(p => p.ProcessId)
            .ToDictionary(g => g.Key, g => g.First());

        Dictionary<uint, AppliedProcessNode> nodes = byId.ToDictionary(
            kv => kv.Key,
            kv => new AppliedProcessNode(kv.Value, FilterName(kv.Value, byId)));

        var roots = new List<AppliedProcessNode>();

        foreach (TrackedProcess process in tracked)
        {
            AppliedProcessNode node = nodes[process.ProcessId];

            // A child only hangs under its parent while that parent is redirected too. When the
            // parent has already exited the child stands on its own rather than disappearing —
            // it is still being redirected, which is the whole point of this list.
            if (process.ParentProcessId != 0
                && nodes.TryGetValue(process.ParentProcessId, out AppliedProcessNode? parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }

    // Which filter put this process under redirection. The tracker remembers it only on the process
    // that actually matched one, so an adopted child has to be traced back up to whichever ancestor
    // did — that is the filter whose action it is being routed by, and the one to go and edit.
    //
    // The walk is bounded rather than trusting the chain to end: pids are reused, and a chain that
    // loops back on itself would hang the window rather than show a wrong name.
    private static string FilterName(TrackedProcess process, IReadOnlyDictionary<uint, TrackedProcess> byId)
    {
        TrackedProcess at = process;

        for (int step = 0; step < MaxParentSteps; step++)
        {
            if (at.MatchedRule != null) return at.MatchedRule.Name;

            if (at.ParentProcessId == 0
                || !byId.TryGetValue(at.ParentProcessId, out TrackedProcess? parent)
                || ReferenceEquals(parent, at)) break;

            at = parent;
        }

        // Nothing matched anywhere up the chain: the caller named this pid itself, through
        // --pid or Launch suspended, so no filter describes it.
        return "—";
    }

    // Deep enough for any real process tree; short enough that a reused pid pointing at its own
    // descendant cannot spin here.
    private const int MaxParentSteps = 64;

    // Adding opens the editor straight away rather than dropping a blank row into the list. A
    // filter that exists but says nothing is a row the user has to notice and then go fix, and an
    // empty condition tree matches nothing, so the row would sit there doing quietly nothing.
    [RelayCommand]
    private void AddRule()
    {
        RoutingPolicy? policy = Policies.FirstOrDefault();
        if (policy is null) return;

        string pattern = SelectedProcess?.Name ?? "program.exe";
        ProcessRule rule = NewRule(policy, pattern, ConditionGroup.CreateDefault(pattern));

        if (!Edit(rule)) return;
        _ = Add(rule);
    }

    [RelayCommand]
    private void AddRuleFromSelection()
    {
        AppliedProcessNode? row = SelectedProcess;
        RoutingPolicy? policy = Policies.FirstOrDefault();
        if (row is null || policy is null) return;

        // Prefer the full path when it is readable: two programs with the same file name are
        // common (every Electron app ships an "app.exe"), and the path says which one is meant.
        var condition = new ConditionGroup
        {
            Children =
            {
                new ProcessNameCondition
                {
                    Matcher = row.Path != null ? ProcessMatcherType.FullPath : ProcessMatcherType.ExeName,
                    Pattern = row.Path ?? row.Name,
                },
            },
        };

        ProcessRule rule = NewRule(policy, row.Name, condition);
        if (!Edit(rule)) return;
        _ = Add(rule);
    }

    [RelayCommand]
    private void EditRule(ProcessFilterRowViewModel? row)
    {
        row ??= SelectedRule;
        if (row is null || !Edit(row.Model)) return;

        // The two cells the dialog changes are the ones the grid cannot edit itself, so the row is
        // asked to read them again. It used to be taken out of the collection and put back — the
        // only way to make WPF re-read a plain model — which unselected it on the way past.
        row.Refresh();
        SelectedRule = row;

        _services.SaveAndApply();
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is null) return;
        _services.Config.ProcessRules.Remove(SelectedRule.Model);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Save() => _services.SaveAndApply();

    // ==== arranging the filters ====
    //
    // The order of the list is not decoration: a process is caught by the FIRST filter that matches
    // it and by no other, so a narrow filter sitting under a wide one never gets a chance. Until
    // the rows could be dragged there was no way to say which came first at all — they stood in the
    // order they happened to be added in.
    //
    // Answered here rather than on the row: the row draws one filter, and which order the filters
    // stand in is the list's business.

    public bool CanAccept(object dragged, object target, DropWhere where)
        => dragged is ProcessFilterRowViewModel moved
        && target is ProcessFilterRowViewModel onto
        && !ReferenceEquals(moved, onto)
        && where is DropWhere.Before or DropWhere.After;

    public void Accept(object dragged, object target, DropWhere where)
    {
        if (!CanAccept(dragged, target, where)) return;

        MoveRuleTo(
            (ProcessFilterRowViewModel)dragged,
            Rules.IndexOf((ProcessFilterRowViewModel)target) + (where == DropWhere.After ? 1 : 0));
    }

    private void MoveRuleTo(ProcessFilterRowViewModel rule, int index)
    {
        int from = Rules.IndexOf(rule);
        if (from < 0) return;

        // The gap the row is asked to fill is counted with the row still in the list, and taking
        // it out closes one place ahead of it.
        if (index > from) index--;

        index = Math.Clamp(index, 0, Rules.Count - 1);
        if (index == from) return;

        Rules.Move(from, index);

        // The grid is a copy of the saved list, and it is the saved order the engine matches
        // against: rearranging only the copy would look right and change nothing about what runs.
        _services.Config.ProcessRules.Clear();
        foreach (ProcessFilterRowViewModel moved in Rules) _services.Config.ProcessRules.Add(moved.Model);

        SelectedRule = rule;
        _services.SaveAndApply();
    }

    // Opens the filter window on a copy and writes the result back only when the user saves.
    private bool Edit(ProcessRule rule)
    {
        var viewModel = new ProcessFilterViewModel(rule, Policies);
        var window = new ProcessFilterWindow(viewModel);

        Window? owner = Application.Current?.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window)) window.Owner = owner;

        if (window.ShowDialog() != true) return false;

        viewModel.ApplyTo(rule);
        return true;
    }

    private static ProcessRule NewRule(RoutingPolicy policy, string name, ProcessCondition condition)
        => new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = name,
            Condition = condition,
            PolicyIds = { policy.Id },
        };

    /// <summary>
    /// Adds a filter and starts saving it. The returned task completes once the engine has taken
    /// the new configuration — which matters to anyone whose next step depends on the rule being
    /// in force, since <see cref="AppServices.SaveAndApply"/> only queues the work.
    /// </summary>
    private Task Add(ProcessRule rule)
    {
        _services.Config.ProcessRules.Add(rule);

        var row = new ProcessFilterRowViewModel(rule);
        Rules.Add(row);
        SelectedRule = row;
        return _services.SaveAndApply();
    }

    // Starts a program suspended, lets the engine attach, then resumes it. This is the only way to
    // guarantee that not a single connection escapes before the redirect is in place.
    [RelayCommand]
    private async Task LaunchSuspendedAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        ISuspendedProcess? suspended = null;
        try
        {
            suspended = new SuspendedProcessLauncher().Launch(dialog.FileName, args: null);

            // A filter must exist for the engine to adopt it, so create one for this exact program
            // unless something the user already wrote would have claimed it anyway. Asked of the
            // matcher rather than of the patterns, because a filter that claims this program by
            // wildcard or by name is just as good and a second one would be noise.
            RoutingPolicy? policy = Policies.FirstOrDefault();
            string name = Path.GetFileNameWithoutExtension(dialog.FileName);
            Task applied = Task.CompletedTask;

            if (policy != null && !_services.Config.ProcessRules.Any(
                    r => ProcessRuleMatcher.IsMatch(r, name, dialog.FileName)))
            {
                ProcessRule rule = NewRule(
                    policy,
                    name,
                    new ConditionGroup
                    {
                        Children =
                        {
                            new ProcessNameCondition
                            {
                                Matcher = ProcessMatcherType.FullPath,
                                Pattern = dialog.FileName,
                            },
                        },
                    });

                applied = Add(rule);
            }

            // Awaited, because SaveAndApply only queues the work. Scanning and resuming without
            // this matched the process against the configuration as it was a moment ago, and the
            // program then ran unredirected until the next scan — the very SYN leak this whole
            // feature exists to close.
            await applied.ConfigureAwait(true);

            // The watcher sees the suspended process on its next scan; resuming only after that
            // is what closes the SYN race.
            await _services.Engine.ForceProcessScanAsync().ConfigureAwait(true);
            suspended.Resume();
            RefreshApplied();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ProxyDivert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            suspended?.Dispose();
        }
    }

    // One process the engine is currently redirecting, plus whatever it dragged in with it.
    public sealed class AppliedProcessNode
    {
        public uint Id { get; }
        public string Name { get; }
        public string? Path { get; }

        /// <summary>
        /// The filter that put this process under redirection — its parent's for an adopted child,
        /// since that is the one that caught it.
        /// </summary>
        /// <remarks>
        /// The policies it routes by are not repeated here. They are a property of the filter, the
        /// grid above lists them against it, and a filter naming three of them made this column the
        /// widest thing in the tab while saying nothing that could be acted on.
        /// </remarks>
        public string Filter { get; }

        public ObservableCollection<AppliedProcessNode> Children { get; }
            = new ObservableCollection<AppliedProcessNode>();

        public AppliedProcessNode(TrackedProcess process, string filterName)
        {
            Id = process.ProcessId;
            Name = process.Name;
            Path = process.ExecutablePath;
            Filter = filterName;
        }
    }
}
