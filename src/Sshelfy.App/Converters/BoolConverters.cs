using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sshelfy.App.Converters;

public static class AppConverters
{
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(count => count == 0);

    public static readonly IValueConverter IsNotZero =
        new FuncValueConverter<int, bool>(count => count != 0);

    public static readonly IValueConverter ToFontFamily =
        new FuncValueConverter<string?, FontFamily>(name => string.IsNullOrWhiteSpace(name) ? FontFamily.Default : new FontFamily(name));

    public static readonly IValueConverter BoolToFontWeight =
        new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);
}
