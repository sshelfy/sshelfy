using System.Text;
using System.Text.RegularExpressions;

namespace Sshelfy.Core.Terminal;

/// <summary>A hyperlink under the pointer, with its extent for underlining (end column exclusive).</summary>
public sealed record TerminalLink(string Uri, long StartLine, int StartColumn, long EndLine, int EndColumn, bool IsExplicit)
{
    public bool Contains(long line, int column) =>
        (line > StartLine || (line == StartLine && column >= StartColumn)) &&
        (line < EndLine || (line == EndLine && column < EndColumn));

    /// <summary>Only web and mail links are opened: remote output must not launch arbitrary programs.</summary>
    public bool IsSafeToOpen =>
        System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" or "mailto";
}

/// <summary>Finds links in the terminal: OSC 8 hyperlinks first, then URLs written as plain text.</summary>
public static partial class TerminalLinks
{
    [GeneratedRegex(@"(?:https?|ftp)://[^\s<>""'`]+|mailto:[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>The link at a cell (stable line number, column), or null. Caller holds <see cref="TerminalEmulator.SyncRoot"/>.</summary>
    public static TerminalLink? FindAt(TerminalEmulator terminal, long lineNumber, int column)
    {
        var index = (int)(lineNumber - terminal.FirstLineNumber);
        if (index < 0 || index >= terminal.TotalLines)
            return null;

        var line = terminal.GetLine(index);
        if (column < 0 || column >= line.Length)
            return null;

        if (line.Cells[column].LinkId is var id and > 0 && terminal.GetLinkUri(id) is { } uri)
            return ExplicitLink(terminal, index, column, id, uri);

        return TextLink(terminal, index, column);
    }

    /// <summary>Cells sharing the same OSC 8 id around the position, following soft wraps.</summary>
    private static TerminalLink ExplicitLink(TerminalEmulator terminal, int index, int column, int id, string uri)
    {
        int startIndex = index, startColumn = column;
        while (true)
        {
            var cells = terminal.GetLine(startIndex).Cells;
            if (startColumn > 0 && cells[startColumn - 1].LinkId == id)
                startColumn--;
            else if (startColumn == 0 && startIndex > 0 && terminal.GetLine(startIndex - 1) is { IsWrapped: true } previous && previous.Cells[^1].LinkId == id)
                (startIndex, startColumn) = (startIndex - 1, previous.Length - 1);
            else
                break;
        }

        int endIndex = index, endColumn = column + 1;
        while (true)
        {
            var line = terminal.GetLine(endIndex);
            if (endColumn < line.Length && line.Cells[endColumn].LinkId == id)
                endColumn++;
            else if (endColumn == line.Length && line.IsWrapped && endIndex + 1 < terminal.TotalLines && terminal.GetLine(endIndex + 1).Cells[0].LinkId == id)
                (endIndex, endColumn) = (endIndex + 1, 1);
            else
                break;
        }

        var first = terminal.FirstLineNumber;
        return new TerminalLink(uri, first + startIndex, startColumn, first + endIndex, endColumn, IsExplicit: true);
    }

    /// <summary>A URL in the logical line (soft-wrapped rows joined) that covers the position.</summary>
    private static TerminalLink? TextLink(TerminalEmulator terminal, int index, int column)
    {
        var top = index;
        while (top > 0 && terminal.GetLine(top - 1).IsWrapped)
            top--;

        var text = new StringBuilder();
        var map = new List<(int Index, int Column)>();
        for (var row = top; row < terminal.TotalLines; row++)
        {
            var line = terminal.GetLine(row);
            for (var c = 0; c < line.Length; c++)
            {
                var cell = line.Cells[c];
                if ((cell.Style.Flags & CellFlags.WideContinuation) != 0)
                    continue;
                var start = text.Length;
                text.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
                for (var i = start; i < text.Length; i++)
                    map.Add((row, c));
            }

            if (!line.IsWrapped)
                break;
        }

        foreach (Match match in UrlPattern().Matches(text.ToString()))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            // A closing parenthesis belongs to the URL only if it opened one, as in Wikipedia links.
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
                url = url[..^1];
            if (url.Length == 0)
                continue;

            var (startIndex, startColumn) = map[match.Index];
            var (endIndex, lastColumn) = map[match.Index + url.Length - 1];
            var first = terminal.FirstLineNumber;
            var link = new TerminalLink(url, first + startIndex, startColumn, first + endIndex, lastColumn + 1, IsExplicit: false);
            if (link.Contains(first + index, column))
                return link;
        }

        return null;
    }
}