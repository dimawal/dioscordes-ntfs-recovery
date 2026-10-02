using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NtfsRecovery.Gui.Converters;

/// <summary>Null (or empty string) -> Collapsed; anything else -> Visible. Used to show/hide the image vs. text preview panes without extra view-model flags.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isEmpty = value is null || (value is string s && string.IsNullOrEmpty(s));
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
