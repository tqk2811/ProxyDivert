using System;
using System.Windows;

namespace ProxyDivert.Themes;

/// <summary>
/// Merges the window's resources (palette, control styles, converters, templates) into
/// Application.Resources while the window is open, and removes them when it closes. The strings
/// stay merged the whole time, since the tray and the view models read them with no window.
/// </summary>
public static class UiResources
{
    private const string DictionaryUri = "pack://application:,,,/ProxyDivert;component/Themes/UiResources.xaml";

    private static ResourceDictionary? _loaded;

    public static bool IsLoaded => _loaded != null;

    /// <summary>Loads them, once; a second call while loaded does nothing.</summary>
    public static void Load(Application application)
    {
        if (_loaded != null) return;

        // Palette first and in front of the styles, which reach its brushes by DynamicResource.
        ThemeManager.LoadPalette();
        _loaded = new ResourceDictionary { Source = new Uri(DictionaryUri, UriKind.Absolute) };
        application.Resources.MergedDictionaries.Add(_loaded);
    }

    public static void Unload(Application application)
    {
        if (_loaded == null) return;

        application.Resources.MergedDictionaries.Remove(_loaded);
        _loaded = null;
        ThemeManager.UnloadPalette();
    }
}
