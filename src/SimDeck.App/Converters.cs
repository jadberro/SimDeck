using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SimDeck.App;

/// <summary>Collapses when the bound value is null or empty. Used for the
/// "update ready" badge, which should occupy no space when absent.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is null || (value is string s && s.Length == 0)
            ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
