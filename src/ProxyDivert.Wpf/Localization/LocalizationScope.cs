using System.ComponentModel;

namespace ProxyDivert.Wpf.Localization
{
    /// <summary>
    /// A single bindable object that changes every time the language does.
    /// </summary>
    /// <remarks>
    /// Static text in XAML uses <c>DynamicResource</c> and re-reads itself, but text produced by a
    /// converter — an enum value's display name, say — has no resource reference to follow, so
    /// nothing tells WPF to run the converter again. Binding to <see cref="Version"/> alongside the
    /// real value gives it that signal.
    ///
    /// The alternative would be re-publishing every option list from the view models, which means
    /// handing each <c>ComboBox</c> a new <c>ItemsSource</c>: a selector that is re-sourced can drop
    /// its selection and write the null straight back through a TwoWay binding, wiping the very
    /// setting the user was looking at. Refreshing the text alone cannot do that.
    /// </remarks>
    public sealed class LocalizationScope : INotifyPropertyChanged
    {
        public static LocalizationScope Instance { get; } = new LocalizationScope();

        private LocalizationScope() { }

        /// <summary>Increments on every language change; bind to it to be re-evaluated.</summary>
        public int Version { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void Bump()
        {
            Version++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        }
    }
}
