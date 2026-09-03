using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Logging;
using ProxyDivert.Wpf.Services;
using TqkLibrary.WinDivert.Logging.Models;

namespace ProxyDivert.Wpf.ViewModels;

// The Log tab. Same polling reasoning as the connection list: with packet tracing on, the logger
// produces thousands of lines a second, and marshalling each one to the UI thread would make the
// window unusable exactly when the user needs to read it.
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);

    // Enough to see what just happened without turning the pane into a memory hog.
    private const int VisibleLines = 1000;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private InMemoryLogStore _store;

    public ObservableCollection<RedirectLogEntry> Entries { get; } = new ObservableCollection<RedirectLogEntry>();

    [ObservableProperty]
    private string _filter = string.Empty;

    public LogViewModel(AppServices services)
    {
        _services = services;
        _store = services.Logs;
        services.LogStoreChanged += OnLogStoreChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void OnLogStoreChanged(InMemoryLogStore store)
    {
        _store = store;
        Refresh();
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
                e.Tag.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || e.Message.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        RedirectLogEntry[] snapshot = lines.Reverse().Take(VisibleLines).ToArray();

        Entries.Clear();
        foreach (RedirectLogEntry entry in snapshot) Entries.Add(entry);
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
        _services.LogStoreChanged -= OnLogStoreChanged;
    }
}
