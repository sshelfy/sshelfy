using System.Text.RegularExpressions;

namespace Sshelfy.Core.Terminal;

/// <summary>Base ANSI colors for highlights; the terminal draws their bright variants, so they follow the color scheme.</summary>
public enum HighlightColor
{
    Red = 1,
    Green = 2,
    Yellow = 3,
    Blue = 4,
    Magenta = 5,
    Cyan = 6,
}

/// <summary>Colors text that matches <see cref="Pattern"/> (a regular expression) and optionally notifies when it appears.</summary>
public sealed record HighlightRule(
    string Pattern,
    HighlightColor Color = HighlightColor.Red,
    bool Background = false,
    bool Notify = false,
    bool IgnoreCase = true,
    bool IsEnabled = true)
{
    public static IReadOnlyList<HighlightRule> Defaults { get; } =
    [
        new(@"\b(ERROR|FATAL|CRITICAL|FAIL(ED|URE)?|Exception)\b", HighlightColor.Red, IgnoreCase: false),
        new(@"\bWARN(ING)?\b", HighlightColor.Yellow, IgnoreCase: false),
    ];
}

/// <param name="Start">First column of the match.</param>
public readonly record struct HighlightSpan(int Start, int Length, HighlightColor Color, bool Background);

/// <summary>Compiled highlight rules. Invalid patterns are skipped; matching is time-limited.</summary>
public sealed class TerminalHighlighter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    private readonly (Regex Regex, HighlightRule Rule)[] _rules;

    public TerminalHighlighter(IEnumerable<HighlightRule> rules)
    {
        _rules = rules
            .Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => (Regex: TryCompile(r), Rule: r))
            .Where(x => x.Regex is not null)
            .Select(x => (x.Regex!, x.Rule))
            .ToArray();
    }

    public bool IsEmpty => _rules.Length == 0;

    public bool HasNotifications => _rules.Any(r => r.Rule.Notify);

    /// <summary>Highlighted ranges of <paramref name="text"/>; with overlaps, earlier rules win.</summary>
    public IReadOnlyList<HighlightSpan> Find(string text)
    {
        if (_rules.Length == 0 || string.IsNullOrWhiteSpace(text))
            return [];

        var spans = new List<HighlightSpan>();
        foreach (var (regex, rule) in _rules)
        {
            try
            {
                for (var match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    if (match.Length > 0 && !spans.Any(s => match.Index < s.Start + s.Length && s.Start < match.Index + match.Length))
                        spans.Add(new HighlightSpan(match.Index, match.Length, rule.Color, rule.Background));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern on this line: leave it plain.
            }
        }

        return spans;
    }

    /// <summary>The first notifying rule matching <paramref name="text"/>, or null.</summary>
    public HighlightRule? Notification(string text)
    {
        foreach (var (regex, rule) in _rules)
        {
            if (!rule.Notify)
                continue;
            try
            {
                if (regex.IsMatch(text))
                    return rule;
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return null;
    }

    /// <summary>Null when <paramref name="pattern"/> is a valid regular expression, otherwise why not.</summary>
    public static string? Validate(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, MatchTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static Regex? TryCompile(HighlightRule rule)
    {
        try
        {
            return new Regex(rule.Pattern, (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
