namespace Sshelfy.Core.Terminal;

/// <summary>A terminal color scheme. Colors are "#RRGGBB"; <see cref="Ansi"/> holds the 16 base colors.</summary>
public sealed record TerminalTheme(
    string Name,
    string Background,
    string Foreground,
    string Cursor,
    IReadOnlyList<string> Ansi,
    string? CursorText = null,
    string? Selection = null)
{
    /// <summary>A light background (decides the selection tint when the theme has none).</summary>
    public bool IsLight => TerminalThemes.Luminance(Background) > 0.5;
}

public static class TerminalThemes
{
    /// <summary>Name shown for "no theme": the terminal follows the app's light or dark look.</summary>
    public const string Automatic = "sshelfy (follows app theme)";

    public static IReadOnlyList<TerminalTheme> BuiltIn { get; } =
    [
        Theme("Dracula", "#282A36", "#F8F8F2", "#F8F8F2",
            "#21222C,#FF5555,#50FA7B,#F1FA8C,#BD93F9,#FF79C6,#8BE9FD,#F8F8F2,#6272A4,#FF6E6E,#69FF94,#FFFFA5,#D6ACFF,#FF92DF,#A4FFFF,#FFFFFF"),
        Theme("Nord", "#2E3440", "#D8DEE9", "#D8DEE9",
            "#3B4252,#BF616A,#A3BE8C,#EBCB8B,#81A1C1,#B48EAD,#88C0D0,#E5E9F0,#4C566A,#BF616A,#A3BE8C,#EBCB8B,#81A1C1,#B48EAD,#8FBCBB,#ECEFF4"),
        Theme("One Dark", "#282C34", "#ABB2BF", "#528BFF",
            "#282C34,#E06C75,#98C379,#E5C07B,#61AFEF,#C678DD,#56B6C2,#ABB2BF,#5C6370,#E06C75,#98C379,#E5C07B,#61AFEF,#C678DD,#56B6C2,#FFFFFF"),
        Theme("Tokyo Night", "#1A1B26", "#C0CAF5", "#C0CAF5",
            "#15161E,#F7768E,#9ECE6A,#E0AF68,#7AA2F7,#BB9AF7,#7DCFFF,#A9B1D6,#414868,#F7768E,#9ECE6A,#E0AF68,#7AA2F7,#BB9AF7,#7DCFFF,#C0CAF5"),
        Theme("Catppuccin Mocha", "#1E1E2E", "#CDD6F4", "#F5E0DC",
            "#45475A,#F38BA8,#A6E3A1,#F9E2AF,#89B4FA,#F5C2E7,#94E2D5,#BAC2DE,#585B70,#F38BA8,#A6E3A1,#F9E2AF,#89B4FA,#F5C2E7,#94E2D5,#A6ADC8"),
        Theme("Gruvbox Dark", "#282828", "#EBDBB2", "#EBDBB2",
            "#282828,#CC241D,#98971A,#D79921,#458588,#B16286,#689D6A,#A89984,#928374,#FB4934,#B8BB26,#FABD2F,#83A598,#D3869B,#8EC07C,#EBDBB2"),
        Theme("Monokai", "#272822", "#F8F8F2", "#F8F8F0",
            "#272822,#F92672,#A6E22E,#F4BF75,#66D9EF,#AE81FF,#A1EFE4,#F8F8F2,#75715E,#F92672,#A6E22E,#F4BF75,#66D9EF,#AE81FF,#A1EFE4,#F9F8F5"),
        Theme("Solarized Dark", "#002B36", "#839496", "#93A1A1",
            "#073642,#DC322F,#859900,#B58900,#268BD2,#D33682,#2AA198,#EEE8D5,#002B36,#CB4B16,#586E75,#657B83,#839496,#6C71C4,#93A1A1,#FDF6E3"),
        Theme("Solarized Light", "#FDF6E3", "#657B83", "#586E75",
            "#073642,#DC322F,#859900,#B58900,#268BD2,#D33682,#2AA198,#EEE8D5,#002B36,#CB4B16,#586E75,#657B83,#839496,#6C71C4,#93A1A1,#FDF6E3"),
        Theme("GitHub Light", "#FFFFFF", "#1F2328", "#1F2328",
            "#24292F,#CF222E,#116329,#4D2D00,#0969DA,#8250DF,#1B7C83,#6E7781,#57606A,#A40E26,#1A7F37,#633C01,#218BFF,#A475F9,#3192AA,#8C959F"),
    ];

    /// <summary>A built-in or custom theme by name; null for <see cref="Automatic"/> or unknown names.</summary>
    public static TerminalTheme? Find(string? name, IEnumerable<TerminalTheme> custom) =>
        string.IsNullOrEmpty(name) || name == Automatic ? null
        : custom.FirstOrDefault(t => t.Name == name) ?? BuiltIn.FirstOrDefault(t => t.Name == name);

    private static TerminalTheme Theme(string name, string background, string foreground, string cursor, string ansi) =>
        new(name, background, foreground, cursor, ansi.Split(','));

    internal static double Luminance(string hex) =>
        TryParse(hex, out var r, out var g, out var b) ? (0.299 * r + 0.587 * g + 0.114 * b) / 255 : 0;

    internal static bool TryParse(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var value))
            return false;
        r = (byte)(value >> 16);
        g = (byte)(value >> 8);
        b = (byte)value;
        return true;
    }
}
