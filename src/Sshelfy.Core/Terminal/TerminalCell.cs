namespace Sshelfy.Core.Terminal;

public enum ColorKind : byte
{
    Default = 0,
    Indexed = 1,
    Rgb = 2,
}

/// <summary>A terminal color: default, one of the 256 palette entries, or 24-bit RGB.</summary>
public readonly record struct TerminalColor(uint Value)
{
    public static TerminalColor Default => default;

    public static TerminalColor Indexed(int index) => new(0x0100_0000u | (byte)index);

    public static TerminalColor Rgb(byte r, byte g, byte b) => new(0x0200_0000u | ((uint)r << 16) | ((uint)g << 8) | b);

    public ColorKind Kind => (ColorKind)(Value >> 24);

    public byte Index => (byte)Value;

    public byte R => (byte)(Value >> 16);

    public byte G => (byte)(Value >> 8);

    public byte B => (byte)Value;
}

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,

    /// <summary>First half of a double-width character.</summary>
    Wide = 1 << 8,

    /// <summary>Second half of a double-width character; has no glyph of its own.</summary>
    WideContinuation = 1 << 9,
}

/// <summary>Graphic rendition shared by cells written with the same SGR state.</summary>
public readonly record struct CellStyle(TerminalColor Foreground, TerminalColor Background, CellFlags Flags)
{
    public static CellStyle Default => default;

    public CellStyle With(CellFlags add, CellFlags remove = CellFlags.None) => this with { Flags = (Flags | add) & ~remove };
}

/// <summary>One character cell. <see cref="Rune"/> 0 means empty (rendered as space).</summary>
public struct TerminalCell
{
    public int Rune;
    public CellStyle Style;

    /// <summary>OSC 8 hyperlink this cell belongs to (see <see cref="TerminalEmulator.GetLinkUri"/>); 0 for none.</summary>
    public int LinkId;

    public TerminalCell(int rune, CellStyle style)
    {
        Rune = rune;
        Style = style;
    }

    public readonly bool IsEmpty => Rune is 0 or ' ';

    public static TerminalCell Blank(CellStyle style) =>
        // Erased cells keep only the background (BCE), like xterm.
        new(0, new CellStyle(TerminalColor.Default, style.Background, CellFlags.None));
}

/// <summary>A row of cells. <see cref="IsWrapped"/> marks a soft wrap into the next row.</summary>
public sealed class TerminalLine
{
    public TerminalLine(int columns, CellStyle fill = default)
    {
        Cells = new TerminalCell[columns];
        if (fill != default)
            Clear(0, columns, fill);
    }

    public TerminalCell[] Cells { get; private set; }

    public bool IsWrapped { get; set; }

    public int Length => Cells.Length;

    public void Clear(int from, int to, CellStyle style)
    {
        from = Math.Clamp(from, 0, Cells.Length);
        to = Math.Clamp(to, 0, Cells.Length);
        var blank = TerminalCell.Blank(style);
        Cells.AsSpan(from, to - from).Fill(blank);
    }

    public void Reset(CellStyle style)
    {
        Clear(0, Cells.Length, style);
        IsWrapped = false;
    }

    public void Resize(int columns)
    {
        if (columns == Cells.Length)
            return;
        var cells = Cells;
        Array.Resize(ref cells, columns);
        Cells = cells;
    }

    /// <summary>Text of the row; trailing blanks trimmed when <paramref name="trimEnd"/> is set.</summary>
    public string GetText(int from = 0, int to = int.MaxValue, bool trimEnd = true)
    {
        to = Math.Min(to, Cells.Length);
        var sb = new System.Text.StringBuilder(Math.Max(0, to - from));
        for (var i = Math.Max(0, from); i < to; i++)
        {
            ref readonly var cell = ref Cells[i];
            if ((cell.Style.Flags & CellFlags.WideContinuation) != 0)
                continue;
            if (cell.Rune == 0)
                sb.Append(' ');
            else
                sb.Append(char.ConvertFromUtf32(cell.Rune));
        }

        return trimEnd ? sb.ToString().TrimEnd() : sb.ToString();
    }
}
