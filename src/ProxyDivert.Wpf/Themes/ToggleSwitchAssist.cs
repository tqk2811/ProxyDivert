using System.Windows;
using System.Windows.Controls.Primitives;

namespace ProxyDivert.Wpf.Themes;

/// <summary>
/// The third state of the engine switch, attached to the control rather than built into it.
/// </summary>
/// <remarks>
/// A ToggleButton has two states and no room for "on its way", which is exactly what switching
/// redirection on is for the seconds the driver takes to open. An attached property is how a
/// template gets told about a state the control itself does not have — the alternative, hanging the
/// flag off Tag, works but leaves the template reading a property that means nothing in particular.
/// </remarks>
public static class ToggleSwitchAssist
{
    /// <summary>
    /// True while the state the switch shows is being reached rather than already true. The
    /// ToggleSwitch template paints the track a third colour when this is set.
    /// </summary>
    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.RegisterAttached(
            "IsBusy", typeof(bool), typeof(ToggleSwitchAssist), new PropertyMetadata(false));

    public static bool GetIsBusy(ToggleButton element) => (bool)element.GetValue(IsBusyProperty);

    public static void SetIsBusy(ToggleButton element, bool value) => element.SetValue(IsBusyProperty, value);
}
