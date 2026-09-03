using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProxyDivert.Wpf.Converters;

// Inverts a bool. Used for "enabled while NOT running" buttons.
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

// Shows an element only while the bound bool is FALSE — the shape a warning banner needs
// ("visible when NOT elevated").
public sealed class FalseToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TrueToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// True when the bound value is null or an empty string. Lets a DataTrigger express "has an error"
// as Value="False", which XAML cannot say with a plain not-equals.
public sealed class IsNullOrEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrEmpty(s));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
