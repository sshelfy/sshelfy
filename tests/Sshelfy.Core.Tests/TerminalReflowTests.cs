using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalReflowTests
{
    private static string Row(TerminalEmulator t, int row) => t.GetScreenLine(row).GetText();

    private static List<string> AllLines(TerminalEmulator t)
    {
        var lines = new List<string>();
        for (var i = 0; i < t.TotalLines; i++)
            lines.Add(t.GetLine(i).GetText());
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    [Fact]
    public void Narrowing_wraps_long_lines_and_keeps_the_cursor_on_the_prompt()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("abcdefghij\r\n$ ");

        t.Resize(5, 4);

        Assert.Equal(["abcde", "fghij", "$"], AllLines(t));
        Assert.True(t.GetLine(0).IsWrapped);
        Assert.False(t.GetLine(1).IsWrapped);
        Assert.Equal("$", Row(t, t.CursorY));
        Assert.Equal(2, t.CursorX);
    }

    [Fact]
    public void Widening_joins_soft_wrapped_rows()
    {
        var t = new TerminalEmulator(5, 4);
        t.Feed("abcdefgh");
        Assert.Equal("abcde", Row(t, 0));

        t.Resize(10, 4);

        Assert.Equal(["abcdefgh"], AllLines(t));
        Assert.False(t.GetScreenLine(0).IsWrapped);
        Assert.Equal((8, 0), (t.CursorX, t.CursorY));

        t.Feed("ij");
        Assert.Equal("abcdefghij", Row(t, 0));
    }

    [Fact]
    public void Hard_line_breaks_are_kept()
    {
        var t = new TerminalEmulator(10, 6);
        t.Feed("abc\r\ndef");

        t.Resize(2, 6);

        Assert.Equal(["ab", "c", "de", "f"], AllLines(t));
    }

    [Fact]
    public void Wide_characters_never_split_across_rows()
    {
        var t = new TerminalEmulator(5, 4);
        t.Feed("ab漢字");
        Assert.Equal("ab漢", Row(t, 0));
        Assert.Equal("字", Row(t, 1));

        t.Resize(6, 4);

        Assert.Equal(["ab漢字"], AllLines(t));

        t.Resize(3, 4);

        Assert.Equal(["ab", "漢", "字"], AllLines(t));
    }

    [Fact]
    public void Pending_wrap_at_the_end_of_a_full_row_survives_a_resize()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("abcde");

        t.Resize(5, 3);
        t.Feed("f");

        Assert.Equal(["abcde", "f"], AllLines(t));
        Assert.True(t.GetLine(0).IsWrapped);
    }

    [Fact]
    public void Scrollback_is_rewrapped_too()
    {
        var t = new TerminalEmulator(6, 2, scrollbackLimit: 20);
        t.Feed("aaaaaabbbbbb\r\nccc\r\n$ ");
        Assert.True(t.ScrollbackCount > 0);

        t.Resize(12, 2);

        Assert.Equal(["aaaaaabbbbbb", "ccc", "$"], AllLines(t));
        Assert.Equal("$", Row(t, t.CursorY));
    }

    [Fact]
    public void Alternate_screen_content_is_not_reflowed()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("prompt$ ");
        t.Feed("\x1b[?1049h");
        t.Feed("full screen app");

        t.Resize(6, 3);
        t.Feed("\x1b[?1049l");

        Assert.Equal("prompt", Row(t, 0));
        Assert.Equal("$", Row(t, 1));
    }
}
