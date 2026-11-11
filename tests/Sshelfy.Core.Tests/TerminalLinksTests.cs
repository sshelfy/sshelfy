using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalLinksTests
{
    private static TerminalLink? At(TerminalEmulator t, long line, int column)
    {
        lock (t.SyncRoot)
            return TerminalLinks.FindAt(t, line, column);
    }

    [Fact]
    public void Finds_a_plain_url_and_trims_trailing_punctuation()
    {
        var t = new TerminalEmulator(80, 5);
        t.Feed("see https://example.com/docs?a=1. thanks");

        var link = At(t, 0, 10);

        Assert.NotNull(link);
        Assert.Equal("https://example.com/docs?a=1", link.Uri);
        Assert.Equal((0L, 4, 0L, 32), (link.StartLine, link.StartColumn, link.EndLine, link.EndColumn));
        Assert.False(link.IsExplicit);
        Assert.Null(At(t, 0, 1));
        Assert.Null(At(t, 0, 34));
    }

    [Fact]
    public void Keeps_balanced_parentheses_and_follows_soft_wraps()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("(https://en.wikipedia.org/wiki/Shell_(computing))");

        var link = At(t, 1, 5);

        Assert.Equal("https://en.wikipedia.org/wiki/Shell_(computing)", link!.Uri);
        Assert.Equal(0, link.StartLine);
        Assert.Equal(2, link.EndLine);
    }

    [Fact]
    public void Osc8_hyperlinks_cover_their_text()
    {
        var t = new TerminalEmulator(80, 5);
        t.Feed("go \u001b]8;;https://example.org/page\u001b\\click here\u001b]8;;\u001b\\ now");

        var link = At(t, 0, 6);

        Assert.NotNull(link);
        Assert.True(link.IsExplicit);
        Assert.Equal("https://example.org/page", link.Uri);
        Assert.Equal((3, 13), (link.StartColumn, link.EndColumn));
        Assert.Null(At(t, 0, 14));
    }

    [Theory]
    [InlineData("https://ok.example", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    public void Only_web_and_mail_links_are_safe_to_open(string uri, bool safe) =>
        Assert.Equal(safe, new TerminalLink(uri, 0, 0, 0, 1, true).IsSafeToOpen);
}