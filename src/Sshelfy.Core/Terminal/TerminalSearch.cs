using System.Text;
using System.Text.RegularExpressions;

namespace Sshelfy.Core.Terminal;

/// <summary>A search hit in stable line numbers (see <see cref="TerminalEmulator.FirstLineNumber"/>); the end column is exclusive.</summary>
public readonly record struct TerminalMatch(long StartLine, int StartColumn, long EndLine, int EndColumn)
{
    public bool Contains(long line, int column) =>
        (line > StartLine || (line == StartLine && column >= StartColumn)) &&
        (line < EndLine || (line == EndLine && column < EndColumn));
}

/// <summary>Finds text in the screen and scrollback. Soft-wrapped rows are searched as one line.</summary>
public static class TerminalSearch
{
    public const int MaxMatches = 10_000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Returns the matches from the oldest line to the newest. The caller must hold
    /// <see cref="TerminalEmulator.SyncRoot"/>. Throws <see cref="ArgumentException"/> for an invalid pattern.
    /// </summary>
    public static IReadOnlyList<TerminalMatch> Find(TerminalEmulator terminal, string query, bool matchCase, bool useRegex)
    {
        if (string.IsNullOrEmpty(query))
            return [];

        var regex = useRegex
            ? new Regex(query, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, RegexTimeout)
            : null;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var matches = new List<TerminalMatch>();
        var text = new StringBuilder();
        var map = new List<CellRef>();
        var first = terminal.FirstLineNumber;
        var total = terminal.TotalLines;
        var index = 0;

        try
        {
            while (index < total && matches.Count < MaxMatches)
            {
                text.Clear();
                map.Clear();
                TerminalLine line;
                do
                {
                    line = terminal.GetLine(index);
                    Append(line, first + index, text, map);
                    index++;
                }
                while (line.IsWrapped && index < total);

                // Trailing blanks are padding, not content.
                var length = text.Length;
                while (length > 0 && text[length - 1] == ' ')
                    length--;
                if (length == 0)
                    continue;

                var logical = text.ToString(0, length);
                if (regex is not null)
                {
                    for (var m = regex.Match(logical); m.Success && matches.Count < MaxMatches; m = m.NextMatch())
                    {
                        if (m.Length > 0)
                            matches.Add(ToMatch(map, m.Index, m.Length));
                    }
                }
                else
                {
                    var at = logical.IndexOf(query, comparison);
                    while (at >= 0 && matches.Count < MaxMatches)
                    {
                        matches.Add(ToMatch(map, at, query.Length));
                        at = logical.IndexOf(query, at + query.Length, comparison);
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological pattern: keep what was found so far.
        }

        return matches;
    }

    private static void Append(TerminalLine line, long number, StringBuilder text, List<CellRef> map)
    {
        var cells = line.Cells;
        for (var column = 0; column < cells.Length; column++)
        {
            var cell = cells[column];
            var flags = cell.Style.Flags;
            if ((flags & CellFlags.WideContinuation) != 0)
                continue;

            var width = (flags & CellFlags.Wide) != 0 ? 2 : 1;
            var start = text.Length;
            if (cell.Rune == 0)
                text.Append(' ');
            else
                text.Append(char.ConvertFromUtf32(cell.Rune));
            for (var i = start; i < text.Length; i++)
                map.Add(new CellRef(number, column, width));
        }
    }

    private static TerminalMatch ToMatch(List<CellRef> map, int index, int length)
    {
        var start = map[index];
        var end = map[index + length - 1];
        return new TerminalMatch(start.Line, start.Column, end.Line, end.Column + end.Width);
    }

    private readonly record struct CellRef(long Line, int Column, int Width);
}
