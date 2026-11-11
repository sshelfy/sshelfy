namespace Sshelfy.Core.Terminal;

/// <summary>Re-wrapping of soft-wrapped lines when the width changes (main screen and scrollback).</summary>
public sealed partial class TerminalEmulator
{
    /// <summary>
    /// Joins rows marked <see cref="TerminalLine.IsWrapped"/> into logical lines and splits them again
    /// at <paramref name="columns"/>, keeping the cursor on the same character. Called while
    /// <see cref="Columns"/> still holds the old width. The alternate screen is left alone: full-screen
    /// apps redraw themselves after a resize.
    /// </summary>
    private void ReflowMain(int columns)
    {
        var oldColumns = Columns;
        var cursor = _altActive ? (_savedMain.X, _savedMain.Y, _savedMain.PendingWrap) : (_cursorX, _cursorY, _pendingWrap);
        var cursorIndex = _main.Count - Rows + cursor.Item2;

        var output = new List<TerminalLine>(_main.Count + 8);
        var logical = new List<TerminalCell>(oldColumns * 2);
        (int Row, int Column, bool Pending)? newCursor = null;

        var index = 0;
        while (index < _main.Count)
        {
            logical.Clear();
            var cursorOffset = -1;
            while (true)
            {
                var line = _main[index];
                var cells = line.Cells;
                var take = Math.Min(cells.Length, oldColumns);
                var continues = line.IsWrapped && index + 1 < _main.Count;

                // A wide character that did not fit left a blank in the last column; it is not content.
                if (continues && take > 0 && cells[take - 1].Rune == 0 &&
                    (cells[take - 1].Style.Flags & CellFlags.WideContinuation) == 0 &&
                    (_main[index + 1].Cells[0].Style.Flags & CellFlags.Wide) != 0)
                {
                    take--;
                }

                if (index == cursorIndex)
                    cursorOffset = logical.Count + Math.Min(cursor.Item1 + (cursor.Item3 ? 1 : 0), take);

                for (var c = 0; c < take; c++)
                    logical.Add(cells[c]);
                index++;
                if (!continues)
                    break;
            }

            var length = logical.Count;
            while (length > 0 && IsPadding(logical[length - 1]))
                length--;
            if (cursorOffset > length)
                length = cursorOffset;

            Split(logical, length, columns, output, cursorOffset, ref newCursor);
        }

        // Blank rows under the cursor were only padding below the prompt.
        if (newCursor is { } placed)
        {
            while (output.Count - 1 > placed.Row && IsBlankRow(output[^1]))
                output.RemoveAt(output.Count - 1);
        }

        while (output.Count < Rows)
            output.Add(new TerminalLine(columns));

        _main.Clear();
        _main.SetCapacity(_scrollbackLimit + Rows);
        var dropped = Math.Max(0, output.Count - _main.Capacity);
        foreach (var line in output)
            _main.Push(line);

        var (row, column, pending) = newCursor ?? (output.Count - 1, 0, false);
        var cursorY = Math.Clamp(row - dropped - (_main.Count - Rows), 0, Rows - 1);
        column = Math.Min(column, columns - 1);
        if (_altActive)
            _savedMain = _savedMain with { X = column, Y = cursorY, PendingWrap = pending };
        else
            (_cursorX, _cursorY, _pendingWrap) = (column, cursorY, pending);
    }

    /// <summary>Cuts one logical line into rows of <paramref name="columns"/> cells, recording where the cursor lands.</summary>
    private static void Split(
        List<TerminalCell> logical,
        int length,
        int columns,
        List<TerminalLine> output,
        int cursorOffset,
        ref (int Row, int Column, bool Pending)? cursor)
    {
        var position = 0;
        while (true)
        {
            var row = new TerminalLine(columns);
            output.Add(row);
            var column = 0;
            while (position < length && column < columns)
            {
                var cell = position < logical.Count ? logical[position] : default;
                var wide = (cell.Style.Flags & CellFlags.Wide) != 0;
                if (wide && column == columns - 1)
                    break; // the wide character moves to the next row, as when it was printed

                if (position == cursorOffset)
                    cursor = (output.Count - 1, column, false);

                row.Cells[column++] = cell;
                position++;
                if (wide && position < logical.Count)
                {
                    row.Cells[column++] = logical[position];
                    position++;
                }
            }

            if (position < length)
            {
                row.IsWrapped = true;
                continue;
            }

            // The cursor sits right after the content: at the end of a full row it waits to wrap.
            if (cursorOffset >= 0 && cursorOffset == position && cursor is null)
                cursor = column == columns ? (output.Count - 1, columns - 1, true) : (output.Count - 1, column, false);
            return;
        }
    }

    private static bool IsPadding(TerminalCell cell) =>
        cell.Rune is 0 or ' ' &&
        cell.Style.Background.Kind == ColorKind.Default &&
        (cell.Style.Flags & (CellFlags.Inverse | CellFlags.WideContinuation | CellFlags.Underline)) == 0;

    private static bool IsBlankRow(TerminalLine line)
    {
        foreach (var cell in line.Cells)
        {
            if (!IsPadding(cell))
                return false;
        }

        return true;
    }
}
