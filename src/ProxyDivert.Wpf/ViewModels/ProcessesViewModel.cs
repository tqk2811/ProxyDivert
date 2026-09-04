using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.Services;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using TqkLibrary.WinDivert.ProcessControl.Models;

namespace ProxyDivert.Wpf.ViewModels;

// The Processes tab: which programs get redirected, plus a live view of what is running so a rule
// can be created from a row instead of typed by hand.
public sealed partial class ProcessesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<ProcessRule> Rules { get; } = new ObservableCollection<ProcessRule>();

    public ObservableCollection<ProcessRow> RunningProcesses { get; } = new ObservableCollection<ProcessRow>();

    public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

    public Array Matchers { get; } = Enum.GetValues(typeof(ProcessMatcherType));

    [ObservableProperty]
    private ProcessRule? _selectedRule;

    [ObservableProperty]
    private ProcessRow? _selectedProcess;

    [ObservableProperty]
    private string _processFilter = string.Empty;

    public ProcessesViewModel(AppServices services)
    {
        _services = services;
        Reload();
        RefreshProcesses();
    }

    public void Reload()
    {
        Rules.Clear();
        foreach (ProcessRule rule in _services.Config.ProcessRules) Rules.Add(rule);

        Policies.Clear();
        foreach (RoutingPolicy policy in _services.Config.Policies) Policies.Add(policy);
    }

    partial void OnProcessFilterChanged(string value) => RefreshProcesses();

    [RelayCommand]
    public void RefreshProcesses()
    {
        var attached = new HashSet<uint>(_services.Engine.TrackedProcesses.Select(p => p.ProcessId));

        RunningProcesses.Clear();
        foreach (ProcessInfo process in new ProcessFinder().ListAll())
        {
            if (!string.IsNullOrWhiteSpace(ProcessFilter)
                && process.Name.IndexOf(ProcessFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            RunningProcesses.Add(new ProcessRow(process, attached.Contains(process.Id)));
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
        ProcessRow? row = SelectedProcess;
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
            RefreshProcesses();
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

    // One row of the running-process list.
    public sealed class ProcessRow
    {
        public uint Id { get; }
        public string Name { get; }
        public string? Path { get; }
        public bool IsAttached { get; }

        public ProcessRow(ProcessInfo info, bool isAttached)
        {
            Id = info.Id;
            Name = info.Name;
            Path = info.ExecutablePath;
            IsAttached = isAttached;
        }
    }
}
