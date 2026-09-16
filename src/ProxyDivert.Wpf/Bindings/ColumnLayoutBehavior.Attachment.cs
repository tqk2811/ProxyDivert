using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.Bindings;

public static partial class ColumnLayoutBehavior
{
    /// <summary>What actually watches one grid: applies the stored widths, then records new ones.</summary>
    /// <remarks>
    /// A DataGrid raises nothing when a column is resized, so each column is watched through its own
    /// <see cref="DataGridColumn.ActualWidthProperty"/> instead. That catches a gripper drag, the
    /// double-click that fits a column to its content, and anything the code does — all of which are
    /// equally "how wide this column is now", which is the only thing worth storing.
    ///
    /// Watching a dependency property this way keeps the object alive through the descriptor, so the
    /// watchers are dropped when the grid unloads and taken up again when it comes back. A tab
    /// switch does exactly that, which is why <see cref="Apply"/> runs on every load rather than
    /// only the first.
    /// </remarks>
    private sealed class Attachment
    {
        private static readonly DependencyPropertyDescriptor ActualWidthDescriptor =
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.ActualWidthProperty, typeof(DataGridColumn));

        private readonly DataGrid _grid;
        private readonly GridLayoutSection _section;
        private readonly List<DataGridColumn> _watched = new List<DataGridColumn>();

        // True while the stored widths are being written back into the columns. Their ActualWidth
        // settles a layout pass later, so this does not cover the whole round trip — it only keeps
        // the apply itself from being read as something the user did.
        private bool _applying;

        public Attachment(DataGrid grid, GridLayoutSection section)
        {
            _grid = grid;
            _section = section;
        }

        public void Attach()
        {
            _grid.Loaded += OnLoaded;
            _grid.Unloaded += OnUnloaded;
            if (_grid.IsLoaded) Bind();
        }

        public void Detach()
        {
            _grid.Loaded -= OnLoaded;
            _grid.Unloaded -= OnUnloaded;
            Unbind();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Bind();

        private void OnUnloaded(object sender, RoutedEventArgs e) => Unbind();

        private void Bind()
        {
            Unbind();
            Apply();

            foreach (DataGridColumn column in _grid.Columns)
            {
                if (string.IsNullOrEmpty(GetColumnKey(column))) continue;

                ActualWidthDescriptor.AddValueChanged(column, OnColumnWidthChanged);
                _watched.Add(column);
            }
        }

        private void Unbind()
        {
            foreach (DataGridColumn column in _watched)
                ActualWidthDescriptor.RemoveValueChanged(column, OnColumnWidthChanged);

            _watched.Clear();
        }

        private void Apply()
        {
            _applying = true;
            try
            {
                foreach (DataGridColumn column in _grid.Columns)
                {
                    string? key = GetColumnKey(column);
                    if (string.IsNullOrEmpty(key)) continue;

                    double? width = _section.WidthOf(key!);

                    // Nothing stored means the width declared in XAML stands. That is the default
                    // arrangement, and it is also what a column gets back when the file is deleted.
                    if (width is null) continue;

                    column.Width = new DataGridLength(width.Value, DataGridLengthUnitType.Pixel);
                }
            }
            finally
            {
                _applying = false;
            }
        }

        private void OnColumnWidthChanged(object? sender, EventArgs e)
        {
            if (_applying || sender is not DataGridColumn column) return;

            string? key = GetColumnKey(column);
            if (string.IsNullOrEmpty(key)) return;

            _section.Record(key!, column.ActualWidth);
        }
    }
}
