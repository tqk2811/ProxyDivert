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

    public ConnectionsViewModel(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
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
