using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ProxyDivert.Services;

/// <summary>
/// How wide the user dragged each column, for every grid in the window, kept in a file of its own
/// next to the executable so an arrangement outlives the process.
/// </summary>
/// <remarks>
/// Deliberately not part of <c>AppConfig</c>, although that file already carries the language and
/// the theme. The configuration the window holds is an edit buffer: nothing in it reaches the file
/// until the user presses Save, and writing it because a column was dragged would commit half-typed
/// rules behind their back. This is also the one piece of state nothing outside the window cares
/// about — losing the file costs a drag, not a setup — so it is written on a best-effort basis and
/// a failure is swallowed rather than reported.
///
/// Writes are debounced: dragging a gripper changes a width on every mouse move, and each of those
/// would otherwise be a file write. The last one wins <see cref="SaveDelay"/> after the drag stops,
/// and <see cref="Flush"/> on the way out covers a drag that was still pending when the window
/// closed.
/// </remarks>
public sealed class GridLayoutStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    // Guards both dictionaries. Widths are recorded on the UI thread and written on a timer
    // thread, so the two never touch the same dictionary without it.
    private readonly object _gate = new object();

    // grid key -> column key -> width in pixels.
    private readonly Dictionary<string, Dictionary<string, double>> _widths;

    private readonly Dictionary<string, GridLayoutSection> _sections
        = new Dictionary<string, GridLayoutSection>(StringComparer.Ordinal);

    private readonly Timer _saveTimer;

    private bool _dirty;
    private bool _disposed;

    public string FilePath { get; }

    public GridLayoutStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath();
        _widths = Load(FilePath);
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Beside the executable, next to the configuration and for the same reason.</summary>
    public static string DefaultFilePath()
        => Path.Combine(AppContext.BaseDirectory, "proxydivert.layout.json");

    /// <summary>
    /// The widths of one grid. The same instance every time for a given key, so a view model may
    /// hand it to the view and a second caller asking later sees the same numbers.
    /// </summary>
    public GridLayoutSection Section(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A grid needs a key.", nameof(key));

        lock (_gate)
        {
            if (!_sections.TryGetValue(key, out GridLayoutSection? section))
            {
                section = new GridLayoutSection(this, key);
                _sections.Add(key, section);
            }
            return section;
        }
    }

    internal double? WidthOf(string sectionKey, string columnKey)
    {
        lock (_gate)
        {
            return _widths.TryGetValue(sectionKey, out Dictionary<string, double>? columns)
                && columns.TryGetValue(columnKey, out double width)
                    ? width
                    : (double?)null;
        }
    }

    internal void Record(string sectionKey, string columnKey, double width)
    {
        // A column that has never been measured reports zero, and NaN comes back from a grid whose
        // layout has not settled. Neither is a width anyone dragged to.
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return;

        // Rounded before the comparison below: WPF hands out fractional widths that differ in the
        // last digit between two layout passes, and storing those would make every tab switch a
        // reason to rewrite the file.
        width = Math.Round(width, 1);

        lock (_gate)
        {
            if (_disposed) return;

            if (!_widths.TryGetValue(sectionKey, out Dictionary<string, double>? columns))
            {
                columns = new Dictionary<string, double>(StringComparer.Ordinal);
                _widths.Add(sectionKey, columns);
            }

            if (columns.TryGetValue(columnKey, out double previous) && previous == width) return;

            columns[columnKey] = width;
            _dirty = true;
        }

        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes a pending change at once. Called on the way out, when the timer has no time left to fire.</summary>
    public void Flush() => SaveNow();

    private void SaveNow()
    {
        string json;

        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_widths, SerializerOptions);
        }

        // Temp file then swap, like the configuration: a crash halfway through a write would
        // otherwise leave a file that parses as nothing and takes every column width with it.
        string tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            // Column widths are not worth an error dialog, let alone taking the application down.
        }
    }

    private static Dictionary<string, Dictionary<string, double>> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

            var stored = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(
                File.ReadAllText(path), SerializerOptions);

            return stored is null
                ? new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal)
                : new Dictionary<string, Dictionary<string, double>>(stored, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Back to the widths declared in XAML. Nothing is kept aside the way a broken config is:
            // there is nothing here the user typed, and the next drag writes the file afresh.
            return new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _saveTimer.Dispose();
        SaveNow();
    }
}
