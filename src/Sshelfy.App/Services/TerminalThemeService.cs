using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Sshelfy.App.Controls;
using Sshelfy.Core.Services;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.Services;

/// <summary>
/// Publishes the terminal palette as the "TerminalPalette" application resource: the chosen color
/// scheme, or with none chosen the light/dark palette from Colors.axaml ("TerminalPalette.Auto").
/// </summary>
public sealed class TerminalThemeService
{
    private const string ResourceKey = "TerminalPalette";
    private const string AutoKey = "TerminalPalette.Auto";
    private readonly ISettingsService _settings;
    private bool _started;

    public TerminalThemeService(ISettingsService settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (_started || Application.Current is not { } app)
            return;
        _started = true;
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(Apply);
        app.ActualThemeVariantChanged += (_, _) => Apply();
        Apply();
    }

    private void Apply()
    {
        var s = _settings.Current;
        var palette = PaletteFor(TerminalThemes.Find(s.TerminalThemeName, s.CustomTerminalThemes));
        if (Application.Current is { } app && palette is not null && !ReferenceEquals(app.Resources.TryGetValue(ResourceKey, out var current) ? current : null, palette))
            app.Resources[ResourceKey] = palette;
    }

    private TerminalTheme? _cachedTheme;
    private TerminalPalette? _cachedPalette;

    /// <summary>The palette for a scheme, or the app's light/dark palette for null.</summary>
    public TerminalPalette? PaletteFor(TerminalTheme? theme)
    {
        if (theme is null)
        {
            var app = Application.Current;
            return app is not null && app.TryGetResource(AutoKey, app.ActualThemeVariant, out var auto) ? auto as TerminalPalette : null;
        }

        if (theme != _cachedTheme)
        {
            _cachedTheme = theme;
            // A hand-edited settings.json with a broken colour falls back to the light/dark palette.
            try
            {
                _cachedPalette = ToPalette(theme);
            }
            catch (FormatException)
            {
                _cachedPalette = PaletteFor(null);
            }
        }

        return _cachedPalette;
    }

    public static TerminalPalette ToPalette(TerminalTheme theme)
    {
        var background = Color.Parse(theme.Background);
        return new TerminalPalette
        {
            Background = background,
            Foreground = Color.Parse(theme.Foreground),
            Cursor = Color.Parse(theme.Cursor),
            CursorText = theme.CursorText is { } cursorText ? Color.Parse(cursorText) : background,
            // Selection is blended over the cell, so an opaque scheme color gets transparency.
            Selection = theme.Selection is { } selection ? WithAlpha(Color.Parse(selection), 0x66)
                : theme.IsLight ? Color.Parse("#3D2F6FE4") : Color.Parse("#4C4C8DFF"),
            Ansi = string.Join(',', theme.Ansi),
        };
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
