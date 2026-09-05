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

// True when the two bound values are the same object. What it is for: a row template asking "am I
// the one being renamed?" — the answer lives on the view model as one reference, not as a flag on
// every row, so the row has to compare itself against it.
public sealed class SameObjectConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
