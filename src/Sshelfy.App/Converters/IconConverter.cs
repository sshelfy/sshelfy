using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Sshelfy.App.ViewModels;

namespace Sshelfy.App.Converters;

/// <summary>Resolves an <see cref="AppIcon"/> (or icon name) to its geometry resource.</summary>
public sealed class IconConverter : IValueConverter
{
    public static IconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value switch
        {
            AppIcon icon when icon != AppIcon.None => icon.ToString(),
            string s when s.Length > 0 => s,
            _ => null,
        };

        if (name is null || Application.Current is not { } app)
            return null;

        return app.TryGetResource("Icon." + name, app.ActualThemeVariant, out var resource) ? resource as Geometry : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
