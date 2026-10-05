using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProxyDivert.Wpf.Bindings;

/// <summary>
/// Lets a TextBox take digits only: typed characters, pasted text and the space bar (which
/// PreviewTextInput never sees) are all refused unless they are 0-9.
/// </summary>
/// <remarks>
/// Digits alone still allow "0" or an empty box; whoever the box is bound to decides what those
/// mean (refused, or "use the default"), since only it knows. This only keeps letters, signs and
/// separators from being typed in the first place.
/// </remarks>
public static class PositiveIntegerInput
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PositiveIntegerInput), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        DataObject.RemovePastingHandler(box, OnPasting);
        if (!(bool)e.NewValue) return;

        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        DataObject.AddPastingHandler(box, OnPasting);
        InputMethod.SetIsInputMethodEnabled(box, false);
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsDigits(e.Text)) e.Handled = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !IsDigits(text.Trim()))
            e.CancelCommand();
    }

    private static bool IsDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text)
            if (c is < '0' or > '9') return false;
        return true;
    }
}
