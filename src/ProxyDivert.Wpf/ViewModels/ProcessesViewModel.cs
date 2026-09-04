using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.Services;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;

namespace ProxyDivert.Wpf.ViewModels;

// The Processes tab: which programs get redirected, and which ones the engine is actually holding
// right now — shown as a tree, because a process adopted through IncludeChildren only makes sense
// underneath the process that dragged it in.
public sealed partial class ProcessesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<ProcessRule> Rules { get; } = new ObservableCollection<ProcessRule>();

    /// <summary>Roots of the redirected-process tree; children hang off <see cref="AppliedProcessNode.Children"/>.</summary>
    public ObservableCollection<AppliedProcessNode> AppliedProcesses { get; }
        = new ObservableCollection<AppliedProcessNode>();

    public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

    public Array Matchers { get; } = Enum.GetValues(typeof(ProcessMatcherType));

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
                p, policyNames.TryGetValue(p.PolicyId, out string? name) ? name : "—"));

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

    [RelayCommand]
    private void AddRule()
    {
        RoutingPolicy? policy = Policies.FirstOrDefault();
        if (policy is null) return;

        var rule = new ProcessRule
        {
            Id = Guid.NewGuid(),
            Matcher = ProcessMatcherType.ExeName,
            Pattern = SelectedProcess?.Name ?? "program.exe",
            PolicyId = policy.Id,
        };
        _services.Config.ProcessRules.Add(rule);
        Rules.Add(rule);
        SelectedRule = rule;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void AddRuleFromSelection()
    {
        AppliedProcessNode? row = SelectedProcess;
        RoutingPolicy? policy = Policies.FirstOrDefault();
        if (row is null || policy is null) return;

        // Prefer the full path when it is readable: two programs with the same file name are
        // common (every Electron app ships an "app.exe"), and the path says which one is meant.
        var rule = new ProcessRule
        {
            Id = Guid.NewGuid(),
            Matcher = row.Path != null ? ProcessMatcherType.FullPath : ProcessMatcherType.ExeName,
            Pattern = row.Path ?? row.Name,
            PolicyId = policy.Id,
        };
        _services.Config.ProcessRules.Add(rule);
        Rules.Add(rule);
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

            // A rule must exist for the engine to adopt it, so create one for this exact program.
            RoutingPolicy? policy = Policies.FirstOrDefault();
            if (policy != null && !_services.Config.ProcessRules.Any(r =>
                    r.Matcher == ProcessMatcherType.FullPath &&
                    string.Equals(r.Pattern, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                var rule = new ProcessRule
                {
                    Id = Guid.NewGuid(),
                    Matcher = ProcessMatcherType.FullPath,
                    Pattern = dialog.FileName,
                    PolicyId = policy.Id,
                };
                _services.Config.ProcessRules.Add(rule);
                Rules.Add(rule);
                _services.SaveAndApply();
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
