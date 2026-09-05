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
public sealed partial class ProcessesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<ProcessRule> Rules { get; } = new ObservableCollection<ProcessRule>();

    /// <summary>Roots of the redirected-process tree; children hang off <see cref="AppliedProcessNode.Children"/>.</summary>
    public ObservableCollection<AppliedProcessNode> AppliedProcesses { get; }
        = new ObservableCollection<AppliedProcessNode>();

    public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

    [ObservableProperty]
    private ProcessRule? _selectedRule;

    [ObservableProperty]
    private AppliedProcessNode? _selectedProcess;

    public ProcessesViewModel(AppServices services)
    {
        _services = services;
        Reload();
        RefreshApplied();
    }

    public void Reload()
    {
        Rules.Clear();
        foreach (ProcessRule rule in _services.Config.ProcessRules) Rules.Add(rule);

        Policies.Clear();
        foreach (RoutingPolicy policy in _services.Config.Policies) Policies.Add(policy);
    }

    [RelayCommand]
    public void RefreshApplied()
    {
        AppliedProcesses.Clear();

        Dictionary<Guid, string> policyNames = Policies
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);

        List<TrackedProcess> tracked = _services.Engine.TrackedProcesses
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();

        Dictionary<uint, AppliedProcessNode> nodes = tracked.ToDictionary(
            p => p.ProcessId,
            p => new AppliedProcessNode(
                p, PolicyNames(p, policyNames)));

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
                AppliedProcesses.Add(node);
            }
        }
    }

    // What the redirected-process list shows in its policy column. A process is routed by the whole
    // list its filter named, in order, so the column says so — the first one alone would hide where
    // half the rules came from.
    private static string PolicyNames(TrackedProcess process, IReadOnlyDictionary<Guid, string> names)
    {
        var found = process.PolicyIds
            .Select(id => names.TryGetValue(id, out string? name) ? name : null)
            .Where(name => name != null)
            .ToList();

        return found.Count > 0 ? string.Join(" → ", found) : "—";
    }

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
        Add(rule);
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
        Add(rule);
    }

    [RelayCommand]
    private void EditRule(ProcessRule? rule)
    {
        rule ??= SelectedRule;
        if (rule is null || !Edit(rule)) return;

        // A ProcessRule is plain data with nothing to raise a change, so the row is put back into
        // the collection to make the grid rebuild it. Cheaper than making the whole model
        // observable for two columns that only change behind a dialog.
        //
        // Out and back in, not assigned over itself: the same reference in and out is not a change
        // as far as WPF is concerned, so the container stays and the cell keeps showing the filter
        // as it was before the edit.
        int index = Rules.IndexOf(rule);
        if (index >= 0)
        {
            Rules.RemoveAt(index);
            Rules.Insert(index, rule);
        }

        SelectedRule = rule;

        _services.SaveAndApply();
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is null) return;
        _services.Config.ProcessRules.Remove(SelectedRule);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Save() => _services.SaveAndApply();

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

    private void Add(ProcessRule rule)
    {
        _services.Config.ProcessRules.Add(rule);
        Rules.Add(rule);
        SelectedRule = rule;
        _services.SaveAndApply();
    }

    // Starts a program suspended, lets the engine attach, then resumes it. This is the only way to
    // guarantee that not a single connection escapes before the redirect is in place.
    [RelayCommand]
    private void LaunchSuspended()
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

                Add(rule);
            }

            // The watcher sees the suspended process on its next scan; resuming only after that
            // is what closes the SYN race.
            _services.Engine.ForceProcessScan();
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

        /// <summary>Name of the policy this process routes through.</summary>
        public string Policy { get; }

        /// <summary>True when no rule named this process: it came along with its parent.</summary>
        public bool IsChild { get; }

        public ObservableCollection<AppliedProcessNode> Children { get; }
            = new ObservableCollection<AppliedProcessNode>();

        public AppliedProcessNode(TrackedProcess process, string policyName)
        {
            Id = process.ProcessId;
            Name = process.Name;
            Path = process.ExecutablePath;
            Policy = policyName;
            IsChild = process.IsChild;
        }
    }
}
