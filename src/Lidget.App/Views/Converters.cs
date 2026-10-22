using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lidget.App.Views;

/// <summary>
/// Converts a percentage to a star <see cref="GridLength"/>. With the parameter <c>rest</c>, it gives
/// the remaining share, so that two grid columns draw a progress bar with rounded ends.
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? Math.Clamp(d, 0, 100) : 0;
        var share = parameter as string == "rest" ? 100 - percent : percent;
        return new GridLength(share, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
