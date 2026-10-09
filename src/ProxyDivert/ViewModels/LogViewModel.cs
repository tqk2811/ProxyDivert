using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Logging;
using ProxyDivert.Services;

namespace ProxyDivert.ViewModels;

// The Log tab. Same polling reasoning as the connection list: with packet tracing on, the logger
// produces thousands of lines a second, and marshalling each one to the UI thread would make the
// window unusable exactly when the user needs to read it.
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);

    // Enough to see what just happened without turning the pane into a memory hog.
    private const int VisibleLines = 1000;

    private readonly DispatcherTimer _timer;
    private readonly InMemoryLogStore _store;

    public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

    [ObservableProperty]
    private string _filter = string.Empty;

    // Bumped from whatever thread logs, read on the UI thread: a tick that finds it unchanged has
    // nothing to show and skips copying the store at all.
    private int _storeVersion;
    private int _shownVersion = -1;

    // The newest line of the store (filtered or not) as of the last refresh. The next tick only
    // has to look at what came after it, instead of clearing and re-adding up to a thousand rows.
    private LogEntry? _newestSeen;

    public LogViewModel(AppServices services)
    {
        _store = services.Logs;
        _store.EntryAdded += OnEntryAdded;

        // Background priority so input, the tray menu and rendering always go first. Not started
        // here: the main view model starts it only while the window is up and this tab is showing.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => RefreshIfChanged();
    }

    /// <summary>
    /// Starts or stops the periodic refresh. Starting refreshes at once, so a pane that was paused
    /// while hidden does not show stale lines for the first interval.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            if (_timer.IsEnabled) return;
            RefreshIfChanged();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnEntryAdded(LogEntry entry) => Interlocked.Increment(ref _storeVersion);

    partial void OnFilterChanged(string value) => Refresh();

    /// <summary>Rebuilds the whole list from the store.</summary>
    [RelayCommand]
    public void Refresh()
    {
        _shownVersion = Volatile.Read(ref _storeVersion);
        IReadOnlyList<LogEntry> all = _store.Snapshot();

        LogEntry[] snapshot = ApplyFilter(all).Reverse().Take(VisibleLines).ToArray();

        Entries.Clear();
        foreach (LogEntry entry in snapshot) Entries.Add(entry);

        _newestSeen = all.Count > 0 ? all[all.Count - 1] : null;
    }

    // The timer's path: nothing when nothing was logged, only the new lines when the last line
    // shown is still in the store, and a full rebuild otherwise.
    private void RefreshIfChanged()
    {
        int version = Volatile.Read(ref _storeVersion);
        if (version == _shownVersion) return;

        IReadOnlyList<LogEntry> all = _store.Snapshot();

        int start = -1;
        if (_newestSeen is not null)
        {
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(all[i], _newestSeen)) { start = i + 1; break; }
            }
        }

        // The last line seen has been pushed out of the store (or there never was one): there is
        // no telling what was lost in between, so start over.
        if (start < 0)
        {
            Refresh();
            return;
        }

        List<LogEntry> added = ApplyFilter(all.Skip(start)).ToList();

        // More new lines than the pane holds: every row would be replaced anyway, and one reset is
        // cheaper than a thousand inserts and removals.
        if (added.Count >= VisibleLines)
        {
            Refresh();
            return;
        }

        _shownVersion = version;
        _newestSeen = all.Count > 0 ? all[all.Count - 1] : _newestSeen;

        // Newest first, the same order a rebuild produces: insert oldest-first at the top so the
        // newest ends up at index 0, then trim the tail back to the cap.
        foreach (LogEntry entry in added) Entries.Insert(0, entry);
        while (Entries.Count > VisibleLines) Entries.RemoveAt(Entries.Count - 1);
    }

    private IEnumerable<LogEntry> ApplyFilter(IEnumerable<LogEntry> lines)
    {
        if (string.IsNullOrWhiteSpace(Filter)) return lines;

        string needle = Filter.Trim();
        return lines.Where(e =>
            e.Category.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
            || e.Message.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [RelayCommand]
    private void Clear()
    {
        _store.Clear();
        Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _store.EntryAdded -= OnEntryAdded;
    }
}
