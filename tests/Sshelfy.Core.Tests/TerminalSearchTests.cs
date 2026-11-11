using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalSearchTests
{
    private static IReadOnlyList<TerminalMatch> Find(TerminalEmulator t, string query, bool matchCase = false, bool regex = false)
    {
        lock (t.SyncRoot)
        {
            return TerminalSearch.Find(t, query, matchCase, regex);
        }
    }

    [Fact]
    public void Finds_every_occurrence_ignoring_case_by_default()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("Error one\r\nok\r\nerror two error");

        var matches = Find(t, "error");

        Assert.Equal(
            [new TerminalMatch(0, 0, 0, 5), new TerminalMatch(2, 0, 2, 5), new TerminalMatch(2, 10, 2, 15)],
            matches);
    }

    [Fact]
    public void Match_case_skips_other_casing()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("Error error");

        Assert.Equal([new TerminalMatch(0, 6, 0, 11)], Find(t, "error", matchCase: true));
    }

    [Fact]
    public void Finds_text_split_across_a_soft_wrap()
    {
        var t = new TerminalEmulator(5, 3);
        t.Feed("abcdefgh");

        Assert.Equal([new TerminalMatch(0, 3, 1, 2)], Find(t, "defg"));
    }

    [Fact]
    public void Does_not_join_hard_line_breaks()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("abc\r\ndef");

        Assert.Empty(Find(t, "cd"));
    }

    [Fact]
    public void Searches_scrollback_with_stable_line_numbers()
    {
        var t = new TerminalEmulator(10, 2, scrollbackLimit: 2);
        t.Feed("needle\r\na\r\nb\r\nc");

        // Four lines written into two rows plus two of scrollback: nothing evicted yet.
        Assert.Equal([new TerminalMatch(0, 0, 0, 6)], Find(t, "needle"));

        t.Feed("\r\nneedle");
        lock (t.SyncRoot)
        {
            Assert.Equal(1, t.FirstLineNumber);
        }

        Assert.Equal([new TerminalMatch(4, 0, 4, 6)], Find(t, "needle"));
    }

    [Fact]
    public void Regex_matches_and_reports_cell_columns_for_wide_characters()
    {
        var t = new TerminalEmulator(20, 2);
        t.Feed("漢字 port 8080");

        Assert.Equal([new TerminalMatch(0, 0, 0, 4)], Find(t, "漢字"));
        Assert.Equal([new TerminalMatch(0, 10, 0, 14)], Find(t, @"\d+", regex: true));
    }

    [Fact]
    public void Invalid_regex_throws_argument_exception()
    {
        var t = new TerminalEmulator(20, 2);
        t.Feed("text");

        Assert.ThrowsAny<ArgumentException>(() => Find(t, "(unclosed", regex: true));
    }

    [Fact]
    public void Empty_query_and_trailing_padding_do_not_match()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("ab");

        Assert.Empty(Find(t, string.Empty));
        Assert.Empty(Find(t, "b "));
    }
}
