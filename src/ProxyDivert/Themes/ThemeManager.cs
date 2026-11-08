using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace ProxyDivert.Themes
{
    /// <summary>
    /// Swaps the active color palette (Dark/Light) at runtime by replacing a single color
    /// ResourceDictionary in Application.Resources, while the window's resources are loaded. All control styles reference brushes via
    /// DynamicResource so they update live. Ported from AndroidSyncControl (persistence removed —
    /// the caller persists the chosen mode).
    /// </summary>
    public static class ThemeManager
    {
        const string DarkUri = "pack://application:,,,/ProxyDivert;component/Themes/Colors.Dark.xaml";
        const string LightUri = "pack://application:,,,/ProxyDivert;component/Themes/Colors.Light.xaml";
        const string PaletteMarkerKey = "Brush.Window.Background";
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        static bool _systemEventsHooked;

        public static ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

        public static ThemeMode Parse(string? value)
            => Enum.TryParse(value, ignoreCase: true, out ThemeMode mode) ? mode : ThemeMode.System;

        public static void Apply(ThemeMode mode)
        {
            CurrentMode = mode;
            ApplyResolvedPalette();
            HookSystemEvents(mode == ThemeMode.System);
        }

        /// <summary>The next mode in the System -> Light -> Dark -> System round, without applying it.</summary>
        public static ThemeMode Next(ThemeMode mode) => mode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        /// <summary>Advances System -> Light -> Dark -> System and applies (does not persist).</summary>
        public static ThemeMode Cycle()
        {
            ThemeMode next = Next(CurrentMode);
            Apply(next);
            return next;
        }

        public static bool IsCurrentlyLight() => CurrentMode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => IsSystemLight(),
        };

        /// <summary>
        /// Puts the palette for the current mode in front of everything else. Called when the UI
        /// resources are loaded for the window; a no-op swap otherwise keeps the idle tray process
        /// from holding a palette nobody is looking at.
        /// </summary>
        public static void LoadPalette() => SwapPalette(IsCurrentlyLight(), insertIfMissing: true);

        /// <summary>Removes the palette again, when the window closes.</summary>
        public static void UnloadPalette()
        {
            var dicts = Application.Current?.Resources.MergedDictionaries;
            var existing = dicts?.FirstOrDefault(d => d.Contains(PaletteMarkerKey));
            if (existing != null)
                dicts!.Remove(existing);
        }

        // Replaces the palette only while one is loaded: with the window closed a mode change is
        // just remembered, and LoadPalette picks it up on the next opening.
        static void ApplyResolvedPalette() => SwapPalette(IsCurrentlyLight(), insertIfMissing: false);

        static void SwapPalette(bool light, bool insertIfMissing)
        {
            var app = Application.Current;
            if (app == null)
                return;

            var dicts = app.Resources.MergedDictionaries;
            var existing = dicts.FirstOrDefault(d => d.Contains(PaletteMarkerKey));
            if (existing == null && !insertIfMissing)
                return;

            var newDict = new ResourceDictionary
            {
                Source = new Uri(light ? LightUri : DarkUri, UriKind.Absolute)
            };

            if (existing != null)
                dicts[dicts.IndexOf(existing)] = newDict;
            else
                dicts.Insert(0, newDict);
        }

        static bool IsSystemLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue("AppsUseLightTheme") is int v)
                    return v != 0;
            }
            catch
            {
                // Registry unavailable -> default light.
            }
            return true;
        }

        static void HookSystemEvents(bool enable)
        {
            if (enable && !_systemEventsHooked)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _systemEventsHooked = true;
            }
            else if (!enable && _systemEventsHooked)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                _systemEventsHooked = false;
            }
        }

        static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || CurrentMode != ThemeMode.System)
                return;
            Application.Current?.Dispatcher.Invoke(ApplyResolvedPalette);
        }
    }
}
