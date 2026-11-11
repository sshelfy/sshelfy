using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Sshelfy.App.Converters;

/// <summary>Turns a tree item level into a left margin.</summary>
public sealed class TreeIndentConverter : IValueConverter
{
    public double Indent { get; set; } = 14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int level ? new Thickness(Math.Max(0, level) * Indent, 0, 0, 0) : new Thickness();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
