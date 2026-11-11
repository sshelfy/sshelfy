using System.Text;
using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalEmulatorTests
{
    private static string Row(TerminalEmulator t, int row) => t.GetScreenLine(row).GetText();

    [Fact]
    public void Prints_text_and_handles_crlf()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("hello\r\nworld");

        Assert.Equal("hello", Row(t, 0));
        Assert.Equal("world", Row(t, 1));
        Assert.Equal((5, 1), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Wraps_at_right_margin_and_marks_soft_wrap()
    {
        var t = new TerminalEmulator(5, 3);
        t.Feed("abcdefg");

        Assert.Equal("abcde", Row(t, 0));
        Assert.True(t.GetScreenLine(0).IsWrapped);
        Assert.Equal("fg", Row(t, 1));
    }

    [Fact]
    public void Pending_wrap_does_not_scroll_until_next_char()
    {
        var t = new TerminalEmulator(3, 2);
        t.Feed("abc");
        Assert.Equal((2, 0), (t.CursorX, t.CursorY));

        t.Feed("\r\n");
        Assert.Equal("abc", Row(t, 0));
        Assert.Equal(1, t.CursorY);
    }

    [Fact]
    public void Scrolling_moves_lines_into_scrollback()
    {
        var t = new TerminalEmulator(10, 2, scrollbackLimit: 100);
        t.Feed("1\r\n2\r\n3\r\n4");

        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("1", t.GetLine(0).GetText());
        Assert.Equal("3", Row(t, 0));
        Assert.Equal("4", Row(t, 1));
    }

    [Fact]
    public void Scrollback_is_capped()
    {
        var t = new TerminalEmulator(10, 2, scrollbackLimit: 3);
        for (var i = 0; i < 20; i++)
            t.Feed($"{i}\r\n");

        Assert.Equal(3, t.ScrollbackCount);
        Assert.True(t.FirstLineNumber > 0);
    }

    [Fact]
    public void Cursor_position_and_erase_in_line()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("0123456789");
        t.Feed("\x1b[1;4H\x1b[K");

        Assert.Equal("012", Row(t, 0));
    }

    [Fact]
    public void Erase_display_clears_screen()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("a\r\nb\r\nc\x1b[2J");

        Assert.All(Enumerable.Range(0, 3), r => Assert.Equal(string.Empty, Row(t, r)));
    }

    [Fact]
    public void Scroll_region_keeps_lines_outside_it()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("top\r\na\r\nb\r\nbottom");
        t.Feed("\x1b[2;3r");      // region rows 2..3
        t.Feed("\x1b[3;1H\n");    // LF at region bottom scrolls only the region

        Assert.Equal("top", Row(t, 0));
        Assert.Equal("b", Row(t, 1));
        Assert.Equal(string.Empty, Row(t, 2));
        Assert.Equal("bottom", Row(t, 3));
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void Alternate_screen_restores_main_screen_and_cursor()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("shell$ ");
        t.Feed("\x1b[?1049h");
        Assert.True(t.IsAlternateScreen);
        t.Feed("\x1b[Hvim stuff");
        Assert.Equal("vim stuff", Row(t, 0));

        t.Feed("\x1b[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("shell$", Row(t, 0));
        Assert.Equal(7, t.CursorX);
    }

    [Fact]
    public void Sgr_sets_indexed_and_truecolor()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("\x1b[1;31mA\x1b[38;5;200mB\x1b[38;2;10;20;30mC\x1b[48:2::1:2:3mD\x1b[0mE");

        var cells = t.GetScreenLine(0).Cells;
        Assert.Equal(TerminalColor.Indexed(1), cells[0].Style.Foreground);
        Assert.True(cells[0].Style.Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(TerminalColor.Indexed(200), cells[1].Style.Foreground);
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cells[2].Style.Foreground);
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), cells[3].Style.Background);
        Assert.Equal(CellStyle.Default, cells[4].Style);
    }

    [Fact]
    public void Wide_characters_take_two_cells()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("a中b");

        var cells = t.GetScreenLine(0).Cells;
        Assert.Equal('中', cells[1].Rune);
        Assert.True(cells[2].Style.Flags.HasFlag(CellFlags.WideContinuation));
        Assert.Equal('b', cells[3].Rune);
        Assert.Equal("a中b", Row(t, 0));
    }

    [Fact]
    public void Utf8_split_across_feeds_is_decoded()
    {
        var t = new TerminalEmulator(10, 2);
        var bytes = Encoding.UTF8.GetBytes("zażółć");
        t.Feed(bytes.AsSpan(0, 3));
        t.Feed(bytes.AsSpan(3));

        Assert.Equal("zażółć", Row(t, 0));
    }

    [Fact]
    public void Osc_title_is_reported()
    {
        var t = new TerminalEmulator(10, 2);
        string? title = null;
        t.TitleChanged += (_, s) => title = s;

        t.Feed("\x1b]0;user@host: ~\x07");

        Assert.Equal("user@host: ~", title);
    }

    [Fact]
    public void Cursor_position_report_is_sent_back()
    {
        var t = new TerminalEmulator(10, 5);
        var output = new List<string>();
        t.Output += b => output.Add(Encoding.UTF8.GetString(b));

        t.Feed("\x1b[3;4H\x1b[6n");

        Assert.Contains("\x1b[3;4R", output);
    }

    [Fact]
    public void Insert_and_delete_characters()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("abcdef\x1b[1;3H\x1b[2@XY");
        Assert.Equal("abXYcdef", Row(t, 0));

        t.Feed("\x1b[1;3H\x1b[2P");
        Assert.Equal("abcdef", Row(t, 0));
    }

    [Fact]
    public void Dec_line_drawing_charset()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("\x1b(0lqk\x1b(B");

        Assert.Equal("┌─┐", Row(t, 0));
    }

    [Fact]
    public void Arrow_keys_follow_application_cursor_mode()
    {
        var t = new TerminalEmulator(10, 2);
        var output = new List<string>();
        t.Output += b => output.Add(Encoding.UTF8.GetString(b));

        t.SendKey(TerminalKey.Up);
        t.Feed("\x1b[?1h");
        t.SendKey(TerminalKey.Up);
        t.SendKey(TerminalKey.Right, TerminalModifiers.Control);

        Assert.Equal(["\x1b[A", "\x1bOA", "\x1b[1;5C"], output);
    }

    [Fact]
    public void Bracketed_paste_wraps_and_normalizes_newlines()
    {
        var t = new TerminalEmulator(10, 2);
        var output = new List<string>();
        t.Output += b => output.Add(Encoding.UTF8.GetString(b));

        t.Feed("\x1b[?2004h");
        t.Paste("a\nb");

        Assert.Equal("\x1b[200~a\rb\x1b[201~", output.Single());
    }

    [Fact]
    public void Submitted_command_is_read_from_the_echoed_line()
    {
        var t = new TerminalEmulator(40, 4);
        string? command = null;
        t.CommandSubmitted += (_, c) => command = c;

        t.Feed("user@host:~$ ");
        t.SendText("ls -la");      // remote echoes it
        t.Feed("ls -la");
        t.SendKey(TerminalKey.Enter);

        Assert.Equal("ls -la", command);
    }

    [Fact]
    public void Tab_completion_result_is_captured()
    {
        var t = new TerminalEmulator(40, 4);
        string? command = null;
        t.CommandSubmitted += (_, c) => command = c;

        t.Feed("$ ");
        t.SendText("cd Doc");
        t.Feed("cd Doc");
        t.SendKey(TerminalKey.Tab);
        t.Feed("uments/");          // shell completes
        t.SendKey(TerminalKey.Enter);

        Assert.Equal("cd Documents/", command);
    }

    [Fact]
    public void Unechoed_input_is_not_captured()
    {
        var t = new TerminalEmulator(40, 4);
        string? command = null;
        t.CommandSubmitted += (_, c) => command = c;

        t.Feed("Password: ");
        t.SendText("secret");      // no echo
        t.SendKey(TerminalKey.Enter);

        Assert.Null(command);
    }

    [Fact]
    public void Selection_text_joins_soft_wrapped_lines()
    {
        var t = new TerminalEmulator(5, 3);
        t.Feed("abcdefg\r\nxy");

        lock (t.SyncRoot)
        {
            Assert.Equal("abcdefg\nxy", t.GetText(0, 0, 2, 5));
        }
    }

    [Fact]
    public void Resize_keeps_cursor_line_visible()
    {
        var t = new TerminalEmulator(10, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");

        t.Resize(10, 3);

        Assert.Equal("5", Row(t, t.CursorY));
        Assert.Equal(3, t.Rows);

        t.Resize(10, 5);
        Assert.Equal("1", Row(t, 0));
        Assert.Equal("5", Row(t, t.CursorY));
    }
}
