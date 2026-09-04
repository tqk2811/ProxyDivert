using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Logging;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.ViewModels;

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

    public LogViewModel(AppServices services)
    {
        _store = services.Logs;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    partial void OnFilterChanged(string value) => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        var lines = _store.Snapshot().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            string needle = Filter.Trim();
            lines = lines.Where(e =>
                e.Category.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || e.Message.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        LogEntry[] snapshot = lines.Reverse().Take(VisibleLines).ToArray();

        Entries.Clear();
        foreach (LogEntry entry in snapshot) Entries.Add(entry);
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
    }
}
