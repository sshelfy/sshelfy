using System.Text;

namespace Sshelfy.Core.Terminal;

public enum CursorShape
{
    Block,
    Underline,
    Bar,
}

public enum MouseTracking
{
    None,

    /// <summary>Button press/release (mode 1000).</summary>
    Normal,

    /// <summary>Press/release plus motion while a button is held (1002).</summary>
    ButtonEvent,

    /// <summary>All motion (1003).</summary>
    AnyEvent,
}

/// <summary>
/// An xterm-compatible terminal screen model. Feed it bytes from the remote side with
/// <see cref="Feed"/>; user input produced through the Send* methods (and protocol responses)
/// come out of <see cref="Output"/>. All members that touch the screen must be called while
/// holding <see cref="SyncRoot"/> (Feed and the Send* methods take it themselves).
/// </summary>
public sealed partial class TerminalEmulator : IVtHandler
{
    private readonly VtParser _parser;
    private readonly int _scrollbackLimit;

    private LineBuffer _main;
    private TerminalLine[] _alt;
    private TerminalLine? _spare;
    private bool _altActive;
    private bool[] _tabStops;

    private int _cursorX;
    private int _cursorY;
    private bool _pendingWrap;
    private CellStyle _style;
    private int _scrollTop;
    private int _scrollBottom;

    private bool _originMode;
    private bool _autoWrap = true;
    private bool _insertMode;
    private bool _lineFeedNewLine;
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;

    private SavedCursor _savedMain;
    private SavedCursor _savedAlt;

    private (long Line, int Column)? _inputStart;

    private const int MaxLinks = 4096;
    private readonly Dictionary<int, string> _links = new();
    private readonly Dictionary<string, int> _linkIds = new(StringComparer.Ordinal);
    private int _activeLink;

    /// <summary>The URI of an OSC 8 hyperlink id stored in <see cref="TerminalCell.LinkId"/>. Caller holds <see cref="SyncRoot"/>.</summary>
    public string? GetLinkUri(int id) => _links.GetValueOrDefault(id);

    public TerminalEmulator(int columns = 80, int rows = 24, int scrollbackLimit = 5000)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _scrollbackLimit = Math.Max(0, scrollbackLimit);
        _parser = new VtParser(this);

        _main = new LineBuffer(_scrollbackLimit + Rows);
        for (var i = 0; i < Rows; i++)
            _main.Push(new TerminalLine(Columns));
        _alt = CreateScreen(Columns, Rows);
        _tabStops = CreateTabStops(Columns);
        _scrollBottom = Rows - 1;
    }

    /// <summary>Lock to hold while reading the screen from another thread.</summary>
    public object SyncRoot { get; } = new();

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorX => _cursorX;

    public int CursorY => _cursorY;

    public bool IsCursorVisible { get; private set; } = true;

    public CursorShape CursorShape { get; private set; } = CursorShape.Block;

    public bool IsCursorBlinking { get; private set; } = true;

    public bool IsAlternateScreen => _altActive;

    public bool IsReverseVideo { get; private set; }

    public bool IsApplicationCursorKeys { get; private set; }

    public bool IsBracketedPaste { get; private set; }

    public bool ReportsFocus { get; private set; }

    public MouseTracking MouseTracking { get; private set; }

    public bool IsSgrMouse { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Lines above the visible screen.</summary>
    public int ScrollbackCount => _altActive ? 0 : _main.Count - Rows;

    /// <summary>Scrollback plus screen lines.</summary>
    public int TotalLines => ScrollbackCount + Rows;

    /// <summary>Stable number of the first line returned by <see cref="GetLine"/>; grows as scrollback is evicted.</summary>
    public long FirstLineNumber => _altActive ? 0 : _main.Evicted;

    /// <summary>Raised (on the feeding thread) after the screen changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Bytes to send to the remote side: user input and protocol replies.</summary>
    public event Action<byte[]>? Output;

    /// <summary>Raised after <see cref="Resize"/> changed the grid size.</summary>
    public event EventHandler? Resized;

    public event EventHandler<string>? TitleChanged;

    public event EventHandler? Bell;

    /// <summary>Remote working directory announced through OSC 7.</summary>
    public event EventHandler<string>? WorkingDirectoryChanged;

    /// <summary>A command line the user submitted, read back from the screen (best effort).</summary>
    public event EventHandler<string>? CommandSubmitted;

    /// <summary>
    /// A main-screen row is leaving the screen for good (scrolled into the scrollback or cleared),
    /// so its text is final. Raised on the feeding thread while <see cref="SyncRoot"/> is held:
    /// handlers must be quick and must not call back into the emulator.
    /// </summary>
    public event Action<string, bool>? LineCompleted;

    /// <summary>
    /// A line feed left a main-screen row: its text as printed so far. Raised on the feeding thread
    /// while <see cref="SyncRoot"/> is held, so handlers must be quick (used for highlight notifications).
    /// </summary>
    public event Action<string>? LineFed;

    /// <summary>Raise <see cref="LineFed"/>; off by default, since building the row text on every line feed costs time under the lock.</summary>
    public bool ReportLineFeeds { get; set; }

    /// <summary>A shell prompt at the end of <paramref name="line"/> ("user@host:~$", "#", "PS C:\>", "❯").</summary>
    public static bool LooksLikePrompt(string line)
    {
        var text = line.TrimEnd();
        return text.Length > 0 && text[^1] is '$' or '#' or '>' or '%' or '❯' or '»';
    }

    /// <summary>Text of the main-screen rows from the top to the cursor (rows not yet completed).</summary>
    public IReadOnlyList<(string Text, bool IsWrapped)> GetPendingLines()
    {
        lock (SyncRoot)
        {
            if (_altActive)
                return [];
            var cursorRow = _cursorY;
            var lines = new List<(string, bool)>();
            for (var row = 0; row <= cursorRow && row < Rows; row++)
            {
                var line = GetScreenLine(row);
                lines.Add((line.GetText(), line.IsWrapped));
            }

            // Trailing blank rows above the cursor add nothing to a log.
            while (lines.Count > 0 && lines[^1].Item1.Length == 0)
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }
    }

    private void CompleteLine(TerminalLine line)
    {
        if (LineCompleted is { } handler)
            handler(line.GetText(), line.IsWrapped);
    }

    /// <summary>Line by index in [0, <see cref="TotalLines"/>): scrollback first, then the screen.</summary>
    public TerminalLine GetLine(int index) => _altActive ? _alt[index] : _main[index];

    public TerminalLine GetScreenLine(int row) => _altActive ? _alt[row] : _main[_main.Count - Rows + row];

    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (SyncRoot)
        {
            _parser.Feed(data);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Feed(string text)
    {
        lock (SyncRoot)
        {
            _parser.Feed(text);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);

        lock (SyncRoot)
        {
            if (columns == Columns && rows == Rows)
                return;

            var reflowed = columns != Columns;
            if (reflowed)
                ReflowMain(columns);

            // Image positions are line numbers; rewrapped text no longer lines up with them.
            _images.RemoveAll(i => i.OnAlternateScreen || reflowed);
            ResizeMain(columns, rows);
            _alt = ResizeScreen(_alt, columns, rows);
            Columns = columns;
            Rows = rows;

            var stops = CreateTabStops(columns);
            Array.Copy(_tabStops, stops, Math.Min(_tabStops.Length, stops.Length));
            _tabStops = stops;

            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _cursorX = Math.Min(_cursorX, columns - 1);
            _cursorY = Math.Clamp(_cursorY, 0, rows - 1);

            // Reflow keeps a pending wrap at the end of a full row; on the alternate screen it is dropped.
            if (!reflowed || _altActive)
                _pendingWrap = false;
            _spare = null;
            _inputStart = null;
        }

        Resized?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeMain(int columns, int rows)
    {
        for (var i = 0; i < _main.Count; i++)
            _main[i].Resize(columns);

        if (rows < Rows)
        {
            // While the alternate screen is shown, the main cursor lives in the saved state.
            var cursorY = _altActive ? _savedMain.Y : _cursorY;
            var excess = Rows - rows;
            var lastRow = Rows - 1;

            // Prefer dropping empty rows below the cursor over pushing content into scrollback.
            while (excess > 0 && lastRow > cursorY && IsBlank(_main[_main.Count - 1]))
            {
                _main.PopLast();
                excess--;
                lastRow--;
            }

            cursorY = Math.Max(0, cursorY - excess);
            if (_altActive)
                _savedMain = _savedMain with { Y = cursorY };
            else
                _cursorY = cursorY;
        }
        else if (rows > Rows)
        {
            var diff = rows - Rows;
            var pull = Math.Min(diff, _main.Count - Rows);
            if (_altActive)
                _savedMain = _savedMain with { Y = _savedMain.Y + pull };
            else
                _cursorY += pull;
            for (var i = pull; i < diff; i++)
                _main.Push(new TerminalLine(columns));
        }

        _main.SetCapacity(_scrollbackLimit + rows);
    }

    private static bool IsBlank(TerminalLine line)
    {
        foreach (var cell in line.Cells)
        {
            if (!cell.IsEmpty)
                return false;
        }

        return true;
    }

    private static TerminalLine[] CreateScreen(int columns, int rows)
    {
        var screen = new TerminalLine[rows];
        for (var i = 0; i < rows; i++)
            screen[i] = new TerminalLine(columns);
        return screen;
    }

    private static TerminalLine[] ResizeScreen(TerminalLine[] old, int columns, int rows)
    {
        var screen = new TerminalLine[rows];
        for (var i = 0; i < rows; i++)
        {
            if (i < old.Length)
            {
                old[i].Resize(columns);
                screen[i] = old[i];
            }
            else
            {
                screen[i] = new TerminalLine(columns);
            }
        }

        return screen;
    }

    private static bool[] CreateTabStops(int columns)
    {
        var stops = new bool[columns];
        for (var i = 8; i < columns; i += 8)
            stops[i] = true;
        return stops;
    }

    private void SetScreenLine(int row, TerminalLine line)
    {
        if (_altActive)
            _alt[row] = line;
        else
            _main[_main.Count - Rows + row] = line;
    }

    private void Send(string text) => Output?.Invoke(Encoding.UTF8.GetBytes(text));

    private readonly record struct SavedCursor(int X, int Y, CellStyle Style, bool OriginMode, bool AutoWrap, bool PendingWrap, bool G0Graphics, bool G1Graphics, bool ShiftOut);
}
