using Sshelfy.Core.Search;

namespace Sshelfy.Core.Tests;

public sealed class FuzzyMatcherTests
{
    [Theory]
    [InlineData("prod", "Production web")]
    [InlineData("pw", "Production web")]
    [InlineData("PRODWEB", "production-web")]
    [InlineData("ngx rl", "nginx -s reload")]
    [InlineData("bc", "abc b")]
    public void Matches_characters_in_order_ignoring_case(string query, string candidate) =>
        Assert.NotNull(FuzzyMatcher.Score(query, candidate));

    [Theory]
    [InlineData("xyz", "Production web")]
    [InlineData("bewp", "Production web")]
    [InlineData("toolong", "tool")]
    public void Rejects_candidates_missing_characters_or_order(string query, string candidate) =>
        Assert.Null(FuzzyMatcher.Score(query, candidate));

    [Fact]
    public void Empty_query_matches_everything_equally() =>
        Assert.Equal(FuzzyMatcher.Score("", "a"), FuzzyMatcher.Score("  ", "something else"));

    [Fact]
    public void Prefix_beats_scattered_match()
    {
        var prefix = FuzzyMatcher.Score("web", "web-01")!.Value;
        var scattered = FuzzyMatcher.Score("web", "work environment backup")!.Value;

        Assert.True(prefix > scattered);
    }

    [Fact]
    public void Word_starts_beat_letters_inside_words()
    {
        var initials = FuzzyMatcher.Score("ns", "New snippet")!.Value;
        var inside = FuzzyMatcher.Score("ns", "Toggle panels")!.Value;

        Assert.True(initials > inside);
    }

    [Fact]
    public void Shorter_candidate_wins_a_tie()
    {
        var shortOne = FuzzyMatcher.Score("ls", "ls")!.Value;
        var longOne = FuzzyMatcher.Score("ls", "ls -la /var/log/nginx/access.log")!.Value;

        Assert.True(shortOne > longOne);
    }
}
