namespace Sshelfy.Core.Search;

/// <summary>
/// Scores how well a short query matches a candidate, like the "go to anything" boxes in editors:
/// the query's characters must appear in order (case-insensitive); matches at word starts,
/// consecutive runs and prefixes rank higher. Spaces in the query only separate words.
/// </summary>
public static class FuzzyMatcher
{
    /// <returns>Null when the candidate does not match; otherwise a score, higher is better.</returns>
    public static int? Score(string query, string candidate)
    {
        query = query.Trim();
        if (query.Length == 0)
            return 0;

        // Jumping to word starts finds "pw" in "Production web" nicely but can skip letters a later
        // character needs ("bc" in "abc b"), so the plain left-most alignment is scored as well.
        var best = Align(query, candidate, preferWordStarts: true) is { } a
            ? Math.Max(a, Align(query, candidate, preferWordStarts: false) ?? int.MinValue)
            : Align(query, candidate, preferWordStarts: false);
        if (best is not { } score)
            return null;

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            score += 20;
        else if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 10;

        // Shorter candidates win ties: "ls" beats "lsblk --all" for the query "ls".
        return score * 4 - Math.Min(candidate.Length, 40) / 10;
    }

    private static int? Align(string query, string candidate, bool preferWordStarts)
    {
        var score = 0;
        var position = 0;
        var previous = -2;
        foreach (var ch in query)
        {
            if (ch == ' ')
            {
                previous = -2;
                continue;
            }

            var index = IndexOf(candidate, ch, position, wordStartOnly: false);
            if (index < 0)
                return null;

            // Keep a consecutive run going; otherwise prefer the next word start if there is one.
            if (preferWordStarts && index != previous + 1 && IndexOf(candidate, ch, position, wordStartOnly: true) is >= 0 and var wordStart)
                index = wordStart;

            score += 1;
            if (index == previous + 1)
                score += 5;
            if (IsWordStart(candidate, index))
                score += 8;
            if (previous >= 0)
                score -= Math.Min(3, index - previous - 1);

            previous = index;
            position = index + 1;
        }

        return score;
    }

    private static int IndexOf(string text, char ch, int start, bool wordStartOnly)
    {
        var upper = char.ToUpperInvariant(ch);
        for (var i = start; i < text.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) == upper && (!wordStartOnly || IsWordStart(text, i)))
                return i;
        }

        return -1;
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
            return true;
        var before = text[index - 1];
        return !char.IsLetterOrDigit(before) || (char.IsLower(before) && char.IsUpper(text[index]));
    }
}
