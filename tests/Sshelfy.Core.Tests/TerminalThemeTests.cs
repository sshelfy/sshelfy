using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalThemeTests
{
    private const string WindowsTerminalScheme = """
        {
            "name": "Campbell Test",
            "background": "#0C0C0C",
            "foreground": "#cccccc",
            "cursorColor": "#FFFFFF",
            "selectionBackground": "#FFFFFF",
            "black": "#0C0C0C", "red": "#C50F1F", "green": "#13A10E", "yellow": "#C19C00",
            "blue": "#0037DA", "purple": "#881798", "cyan": "#3A96DD", "white": "#CCCCCC",
            "brightBlack": "#767676", "brightRed": "#E74856", "brightGreen": "#16C60C", "brightYellow": "#F9F1A5",
            "brightBlue": "#3B78FF", "brightPurple": "#B4009E", "brightCyan": "#61D6D6", "brightWhite": "#F2F2F2"
        }
        """;

    [Fact]
    public void Imports_a_windows_terminal_scheme()
    {
        var theme = Assert.Single(TerminalThemeImporter.Import(WindowsTerminalScheme, "campbell.json"));

        Assert.Equal("Campbell Test", theme.Name);
        Assert.Equal("#0C0C0C", theme.Background);
        Assert.Equal("#CCCCCC", theme.Foreground);
        Assert.Equal("#FFFFFF", theme.Cursor);
        Assert.Equal("#FFFFFF", theme.Selection);
        Assert.Equal(16, theme.Ansi.Count);
        Assert.Equal("#C50F1F", theme.Ansi[1]);
        Assert.Equal("#F2F2F2", theme.Ansi[15]);
        Assert.False(theme.IsLight);
    }

    [Fact]
    public void Imports_every_scheme_from_a_settings_file_with_comments()
    {
        var settings = $$"""
            // Windows Terminal settings
            {
                "profiles": { "list": [] },
                "schemes": [
                    {{WindowsTerminalScheme}},
                    {{WindowsTerminalScheme.Replace("Campbell Test", "Second")}},
                    { "name": "Broken", "background": "#000000" },
                ],
            }
            """;

        var themes = TerminalThemeImporter.Import(settings, "settings.json");

        Assert.Equal(["Campbell Test", "Second"], themes.Select(t => t.Name));
    }

    [Fact]
    public void Imports_an_iterm2_color_file()
    {
        static string Color(string key, double r, double g, double b) =>
            FormattableString.Invariant($"<key>{key}</key><dict><key>Blue Component</key><real>{b}</real><key>Green Component</key><real>{g}</real>") +
            FormattableString.Invariant($"<key>Red Component</key><real>{r}</real><key>Color Space</key><string>sRGB</string></dict>");

        var colors = string.Concat(Enumerable.Range(0, 16).Select(i => Color($"Ansi {i} Color", i / 15.0, 0, 1)));
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            {colors}
            {Color("Background Color", 1, 1, 1)}
            {Color("Foreground Color", 0, 0, 0)}
            {Color("Cursor Color", 1, 0, 0)}
            </dict></plist>
            """;

        var theme = Assert.Single(TerminalThemeImporter.Import(plist, @"C:\Downloads\Paper White.itermcolors"));

        Assert.Equal("Paper White", theme.Name);
        Assert.Equal("#FFFFFF", theme.Background);
        Assert.Equal("#000000", theme.Foreground);
        Assert.Equal("#FF0000", theme.Cursor);
        Assert.Equal("#0000FF", theme.Ansi[0]);
        Assert.Equal("#FF00FF", theme.Ansi[15]);
        Assert.True(theme.IsLight);
    }

    [Fact]
    public void Imports_a_base16_scheme()
    {
        var yaml = string.Join('\n', new[] { "scheme: \"Ocean\"", "author: \"Chris Kempson\"" }
            .Concat(Enumerable.Range(0, 16).Select(i => $"base0{i:X}: \"{i:X}{i:X}{i:X}{i:X}{i:X}{i:X}\"")));

        var theme = Assert.Single(TerminalThemeImporter.Import(yaml, "ocean.yaml"));

        Assert.Equal("Ocean", theme.Name);
        Assert.Equal("#000000", theme.Background);
        Assert.Equal("#555555", theme.Foreground);
        Assert.Equal("#888888", theme.Ansi[1]); // base08 is red
        Assert.Equal("#777777", theme.Ansi[15]); // base07
    }

    [Theory]
    [InlineData("{ \"name\": \"x\" }", "x.json")]
    [InlineData("{ not json", "x.json")]
    [InlineData("<plist><dict></dict></plist>", "x.itermcolors")]
    [InlineData("just some text", "notes.txt")]
    public void Files_without_a_scheme_are_rejected(string content, string fileName) =>
        Assert.Throws<TerminalThemeImportException>(() => TerminalThemeImporter.Import(content, fileName));

    [Fact]
    public void Custom_schemes_win_over_built_in_ones_and_automatic_is_none()
    {
        var custom = TerminalThemes.BuiltIn[0] with { Background = "#123456" };

        Assert.Null(TerminalThemes.Find(TerminalThemes.Automatic, [custom]));
        Assert.Null(TerminalThemes.Find(null, [custom]));
        Assert.Null(TerminalThemes.Find("Missing", [custom]));
        Assert.Equal("#123456", TerminalThemes.Find(custom.Name, [custom])!.Background);
        Assert.Same(TerminalThemes.BuiltIn[1], TerminalThemes.Find(TerminalThemes.BuiltIn[1].Name, [custom]));
        Assert.All(TerminalThemes.BuiltIn, t => Assert.Equal(16, t.Ansi.Count));
    }
}
