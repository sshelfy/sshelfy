using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalHighlighterTests
{
    [Fact]
    public void Default_rules_color_errors_and_warnings()
    {
        var highlighter = new TerminalHighlighter(HighlightRule.Defaults);

        var spans = highlighter.Find("12:00 ERROR db down, WARN retrying, error lowercase stays");

        Assert.Equal(
            [new HighlightSpan(6, 5, HighlightColor.Red, false), new HighlightSpan(21, 4, HighlightColor.Yellow, false)],
            spans);
    }

    [Fact]
    public void Earlier_rules_win_overlaps_and_case_follows_the_rule()
    {
        var highlighter = new TerminalHighlighter(
        [
            new HighlightRule("timeout", HighlightColor.Magenta, Background: true),
            new HighlightRule(@"time\w*", HighlightColor.Blue),
        ]);

        var spans = highlighter.Find("TIMEOUT at time 5");

        Assert.Equal(
            [new HighlightSpan(0, 7, HighlightColor.Magenta, true), new HighlightSpan(11, 4, HighlightColor.Blue, false)],
            spans);
    }

    [Fact]
    public void Disabled_empty_and_invalid_rules_are_skipped()
    {
        var highlighter = new TerminalHighlighter(
        [
            new HighlightRule("ok", IsEnabled: false),
            new HighlightRule(""),
            new HighlightRule("(unclosed"),
        ]);

        Assert.True(highlighter.IsEmpty);
        Assert.Empty(highlighter.Find("ok (unclosed"));
        Assert.NotNull(TerminalHighlighter.Validate("(unclosed"));
        Assert.Null(TerminalHighlighter.Validate(@"\bERROR\b"));
    }

    [Fact]
    public void Only_notifying_rules_trigger()
    {
        var highlighter = new TerminalHighlighter(
        [
            new HighlightRule("ERROR"),
            new HighlightRule("deploy finished", HighlightColor.Green, Notify: true),
        ]);

        Assert.True(highlighter.HasNotifications);
        Assert.Null(highlighter.Notification("ERROR in build"));
        Assert.Equal("deploy finished", highlighter.Notification("[ok] Deploy finished in 3s")?.Pattern);
    }

    [Fact]
    public void Line_feeds_report_the_row_being_left()
    {
        var terminal = new TerminalEmulator(40, 5) { ReportLineFeeds = true };
        var lines = new List<string>();
        terminal.LineFed += lines.Add;

        terminal.Feed("first line\r\nsecond\r\n\x1b[?1049hfull screen app\r\n\x1b[?1049lthird");

        Assert.Equal(["first line", "second"], lines);
    }

    [Theory]
    [InlineData("deploy@web:~/app$ ", true)]
    [InlineData("root@box:/# ", true)]
    [InlineData(@"PS C:\Users\me> ", true)]
    [InlineData("~/src ❯ ", true)]
    [InlineData("Downloading 45%", true)] // a false positive the output pause check has to absorb
    [InlineData("Compiling module 3 of 9", false)]
    [InlineData("", false)]
    public void Recognizes_shell_prompts(string line, bool expected) =>
        Assert.Equal(expected, TerminalEmulator.LooksLikePrompt(line));
}
