using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.Controls;

/// <summary>
/// Colors used by <see cref="TerminalView"/>. Declared per theme variant in Colors.axaml, so the
/// terminal follows light/dark mode. <see cref="Ansi"/> takes the 16 base colors as a
/// comma-separated list; the rest of the 256-color table is computed.
/// </summary>
public sealed class TerminalPalette
{
    private readonly Color[] _table = new Color[256];
    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = new();
    private string _ansi = string.Empty;

    public TerminalPalette()
    {
        BuildExtendedTable();
    }

    public Color Foreground { get; set; } = Color.Parse("#D4D4D8");

    public Color Background { get; set; } = Color.Parse("#18181A");

    public Color Cursor { get; set; } = Color.Parse("#E4E4E7");

    public Color CursorText { get; set; } = Color.Parse("#18181A");

    public Color Selection { get; set; } = Color.Parse("#334C8DFF");

    public string Ansi
    {
        get => _ansi;
        set
        {
            _ansi = value;
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < Math.Min(16, parts.Length); i++)
                _table[i] = Color.Parse(parts[i]);
            _brushes.Clear();
        }
    }

    public Color Resolve(TerminalColor color, bool foreground) => color.Kind switch
    {
        ColorKind.Indexed => _table[color.Index],
        ColorKind.Rgb => Color.FromRgb(color.R, color.G, color.B),
        _ => foreground ? Foreground : Background,
    };

    public IImmutableSolidColorBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color.ToUInt32(), out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _brushes[color.ToUInt32()] = brush;
        }

        return brush;
    }

    /// <summary>Colors 16–231 (6×6×6 cube) and 232–255 (grayscale), as in xterm.</summary>
    private void BuildExtendedTable()
    {
        byte[] levels = [0, 95, 135, 175, 215, 255];
        for (var i = 0; i < 216; i++)
            _table[16 + i] = Color.FromRgb(levels[i / 36], levels[i / 6 % 6], levels[i % 6]);
        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            _table[232 + i] = Color.FromRgb(v, v, v);
        }
    }
}
