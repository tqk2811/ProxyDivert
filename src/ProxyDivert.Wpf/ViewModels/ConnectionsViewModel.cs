using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.ViewModels;

// The Connections tab: what is flowing right now, and where it is going.
//
// Rows are refreshed on a timer rather than pushed per connection: a browser opens connections
// faster than a DataGrid can be told about them one at a time, and a byte counter that updates
// four times a second reads exactly the same to a human.
public sealed partial class ConnectionsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<ConnectionInfo> Connections { get; } = new ObservableCollection<ConnectionInfo>();

    [ObservableProperty]
    private bool _showClosed;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private int _activeCount;

    /// <summary>How wide this tab's columns were left, kept between runs.</summary>
    public GridLayoutSection ColumnLayout { get; }

    public ConnectionsViewModel(AppServices services)
    {
        _services = services;
        ColumnLayout = services.GridLayout.Section("connections");
        // Background priority so input, the tray menu and rendering always go first. Not started
        // here: the main view model starts it only while the window is up and this tab is showing,
        // since rebuilding 500 rows four times a second for nobody still costs the UI thread.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>
    /// Starts or stops the periodic refresh. Starting refreshes at once, so a list that was paused
    /// while hidden does not show stale rows for the first interval.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            if (_timer.IsEnabled) return;
            Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    partial void OnShowClosedChanged(bool value) => Refresh();

    partial void OnFilterChanged(string value) => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        var rows = _services.Engine.Connections.Active.AsEnumerable();
        if (ShowClosed) rows = rows.Concat(_services.Engine.Connections.History);

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            string needle = Filter.Trim();
            rows = rows.Where(c =>
                (c.Host?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                || c.ProcessName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || c.Destination.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || c.OutboundName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        ConnectionInfo[] snapshot = rows.OrderByDescending(c => c.StartedUtc).Take(500).ToArray();

        // Replace wholesale: comparing two lists item by item costs more than rebuilding one that
        // is capped at 500 rows.
        Connections.Clear();
        foreach (ConnectionInfo connection in snapshot) Connections.Add(connection);

        ActiveCount = _services.Engine.Connections.ActiveCount;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _services.Engine.Connections.ClearHistory();
        Refresh();
    }

    public void Dispose() => _timer.Stop();
}
