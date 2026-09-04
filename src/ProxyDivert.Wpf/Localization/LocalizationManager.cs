using System.Globalization;
using System.Linq;
using System.Windows;

namespace ProxyDivert.Wpf.Localization
{
    /// <summary>
    /// Swaps the active string ResourceDictionary (English/Vietnamese) at runtime by replacing a
    /// single dictionary in Application.Resources — mirrors <see cref="Themes.ThemeManager"/>. XAML
    /// references strings via DynamicResource so static text updates live; ViewModels that compose
    /// strings in code subscribe to <see cref="LanguageChanged"/> and rebuild.
    /// </summary>
    public static class LocalizationManager
    {
        const string EnUri = "pack://application:,,,/ProxyDivert;component/Localization/Strings.en.xaml";
        const string ViUri = "pack://application:,,,/ProxyDivert;component/Localization/Strings.vi.xaml";

        /// <summary>Key present in every strings dictionary so we can find and replace the active one.</summary>
        const string MarkerKey = "Str._Marker";

        public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.System;

        /// <summary>Raised after the active language changes so VMs can rebuild composed strings.</summary>
        public static event Action? LanguageChanged;

        public static AppLanguage Parse(string? value)
            => Enum.TryParse(value, ignoreCase: true, out AppLanguage lang) ? lang : AppLanguage.System;

        public static void Apply(AppLanguage language)
        {
            CurrentLanguage = language;
            SwapDictionary(IsCurrentlyVietnamese());
            // Bindings that go through a converter re-evaluate off this; the swap above only
            // reaches the ones written as DynamicResource.
            LocalizationScope.Instance.Bump();
            LanguageChanged?.Invoke();
        }

        public static bool IsCurrentlyVietnamese() => CurrentLanguage switch
        {
            AppLanguage.Vietnamese => true,
            AppLanguage.English => false,
            _ => IsSystemVietnamese(),
        };

        /// <summary>True when the OS UI culture is Vietnamese (what <see cref="AppLanguage.System"/> resolves to).</summary>
        public static bool IsSystemVietnamese()
            => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                   .Equals("vi", StringComparison.OrdinalIgnoreCase);

        /// <summary>Native display name of the language <see cref="AppLanguage.System"/> currently resolves to.</summary>
        public static string SystemLanguageName => IsSystemVietnamese() ? "Tiếng Việt" : "English";

        static void SwapDictionary(bool vietnamese)
        {
            var app = Application.Current;
            if (app == null)
                return;

            var newDict = new ResourceDictionary
            {
                Source = new Uri(vietnamese ? ViUri : EnUri, UriKind.Absolute)
            };

            var dicts = app.Resources.MergedDictionaries;
            var existing = dicts.FirstOrDefault(d => d.Contains(MarkerKey));
            if (existing != null)
                dicts[dicts.IndexOf(existing)] = newDict;
            else
                dicts.Add(newDict);
        }

        /// <summary>Looks up a localized string by key from the active dictionary; returns the key itself if missing.</summary>
        public static string Get(string key)
            => Application.Current?.TryFindResource(key) as string ?? key;

        /// <summary>Looks up a format string and applies <see cref="string.Format(string, object?[])"/>.</summary>
        public static string Format(string key, params object?[] args)
            => string.Format(Get(key), args);

        /// <summary>
        /// Localized display text for an enum value via key "Enum.&lt;TypeName&gt;.&lt;Value&gt;".
        /// Falls back to the enum identifier when no key exists (fine for protocol names). Null -> "—".
        /// </summary>
        public static string EnumText(object? value)
        {
            if (value == null)
                return "—";
            var type = value.GetType();
            if (!type.IsEnum)
                return value.ToString() ?? string.Empty;
            var res = Application.Current?.TryFindResource($"Enum.{type.Name}.{value}") as string;
            return res ?? value.ToString() ?? string.Empty;
        }
    }

    /// <summary>Terse alias for <see cref="LocalizationManager"/> lookups used at call sites.</summary>
    public static class Loc
    {
        public static string S(string key) => LocalizationManager.Get(key);
        public static string F(string key, params object?[] args) => LocalizationManager.Format(key, args);
    }
}
