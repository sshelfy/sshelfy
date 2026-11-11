namespace Sshelfy.Core.Terminal;

/// <summary>Interpretation of parsed control functions (the IVtHandler side).</summary>
public sealed partial class TerminalEmulator
{
    // DEC Special Graphics for 0x5F..0x7E (line drawing used by older curses apps).
    private const string DecGraphics = " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    void IVtHandler.Print(int rune)
    {
        var graphics = _shiftOut ? _g1Graphics : _g0Graphics;
        if (graphics && rune is >= 0x5F and <= 0x7E)
            rune = DecGraphics[rune - 0x5F];

        var width = CharWidth.Of(rune);
        if (width == 0)
            return; // Combining marks are not composed; dropping them keeps the grid aligned.

        if (_pendingWrap)
        {
            if (_autoWrap)
            {
                GetScreenLine(_cursorY).IsWrapped = true;
                _cursorX = 0;
                LineFeed();
            }

            _pendingWrap = false;
        }

        if (width == 2 && _cursorX == Columns - 1)
        {
            if (!_autoWrap)
                return;
            GetScreenLine(_cursorY).Clear(_cursorX, Columns, _style);
            GetScreenLine(_cursorY).IsWrapped = true;
            _cursorX = 0;
            LineFeed();
        }

        var line = GetScreenLine(_cursorY);
        var cells = line.Cells;

        if (_insertMode)
        {
            var shift = Math.Min(width, Columns - _cursorX);
            Array.Copy(cells, _cursorX, cells, _cursorX + shift, Columns - _cursorX - shift);
        }

        BreakWidePair(cells, _cursorX);
        if (width == 2)
            BreakWidePair(cells, _cursorX + 1);

        cells[_cursorX] = new TerminalCell(rune, width == 2 ? _style.With(CellFlags.Wide) : _style) { LinkId = _activeLink };
        if (width == 2)
            cells[_cursorX + 1] = new TerminalCell(0, _style.With(CellFlags.WideContinuation, CellFlags.Wide)) { LinkId = _activeLink };

        _cursorX += width;
        if (_cursorX >= Columns)
        {
            _cursorX = Columns - 1;
            _pendingWrap = _autoWrap;
        }
    }

    /// <summary>Clears the other half of a wide character that is about to be overwritten.</summary>
    private void BreakWidePair(TerminalCell[] cells, int x)
    {
        if (x < 0 || x >= cells.Length)
            return;

        var flags = cells[x].Style.Flags;
        if ((flags & CellFlags.WideContinuation) != 0 && x > 0)
            cells[x - 1] = TerminalCell.Blank(_style);
        else if ((flags & CellFlags.Wide) != 0 && x + 1 < cells.Length)
            cells[x + 1] = TerminalCell.Blank(_style);
    }

    void IVtHandler.Execute(char control)
    {
        switch (control)
        {
            case '\a':
                Bell?.Invoke(this, EventArgs.Empty);
                break;
            case '\b':
                if (_pendingWrap)
                    _pendingWrap = false;
                else if (_cursorX > 0)
                    _cursorX--;
                break;
            case '\t':
                TabForward(1);
                break;
            case '\n' or '\v' or '\f':
                if (ReportLineFeeds && !_altActive && LineFed is { } fed)
                    fed(GetScreenLine(_cursorY).GetText());
                LineFeed();
                if (_lineFeedNewLine)
                    _cursorX = 0;
                break;
            case '\r':
                _cursorX = 0;
                _pendingWrap = false;
                break;
            case '\x0E':
                _shiftOut = true;
                break;
            case '\x0F':
                _shiftOut = false;
                break;
        }
    }

    void IVtHandler.EscDispatch(ReadOnlySpan<char> intermediates, char final)
    {
        if (intermediates.Length == 0)
        {
            switch (final)
            {
                case '7':
                    SaveCursor();
                    break;
                case '8':
                    RestoreCursor();
                    break;
                case 'D':
                    LineFeed();
                    break;
                case 'E':
                    _cursorX = 0;
                    LineFeed();
                    break;
                case 'H':
                    _tabStops[_cursorX] = true;
                    break;
                case 'M':
                    ReverseIndex();
                    break;
                case 'c':
                    FullReset();
                    break;
                case '=' or '>':
                    break; // Keypad modes: numeric keypad is always sent as text.
            }

            return;
        }

        switch (intermediates[0])
        {
            case '(':
                _g0Graphics = final == '0';
                break;
            case ')':
                _g1Graphics = final == '0';
                break;
            case '#' when final == '8':
                // DECALN: fill the screen with 'E'.
                for (var y = 0; y < Rows; y++)
                {
                    var cells = GetScreenLine(y).Cells;
                    for (var x = 0; x < Columns; x++)
                        cells[x] = new TerminalCell('E', default);
                }

                break;
        }
    }

    void IVtHandler.CsiDispatch(VtParams p, char privateMarker, ReadOnlySpan<char> intermediates, char final)
    {
        if (privateMarker == '?')
        {
            if (final is 'h' or 'l')
                SetPrivateModes(p, final == 'h');
            return;
        }

        if (privateMarker == '>')
        {
            if (final == 'c')
                Send("\x1b[>0;10;1c"); // DA2: VT100-class, firmware 10.
            return;
        }

        if (intermediates.Length > 0)
        {
            if (intermediates[0] == ' ' && final == 'q')
                SetCursorStyle(p.Get(0, 1));
            else if (intermediates[0] == '!' && final == 'p')
                SoftReset();
            return;
        }

        switch (final)
        {
            case '@':
                InsertChars(p.Get(0, 1));
                break;
            case 'A':
                MoveCursor(0, -p.Get(0, 1));
                break;
            case 'B' or 'e':
                MoveCursor(0, p.Get(0, 1));
                break;
            case 'C' or 'a':
                MoveCursor(p.Get(0, 1), 0);
                break;
            case 'D':
                MoveCursor(-p.Get(0, 1), 0);
                break;
            case 'E':
                MoveCursor(0, p.Get(0, 1));
                _cursorX = 0;
                break;
            case 'F':
                MoveCursor(0, -p.Get(0, 1));
                _cursorX = 0;
                break;
            case 'G' or '`':
                SetCursorX(p.Get(0, 1) - 1);
                break;
            case 'H' or 'f':
                SetCursor(p.Get(1, 1) - 1, p.Get(0, 1) - 1);
                break;
            case 'I':
                TabForward(p.Get(0, 1));
                break;
            case 'J':
                EraseInDisplay(Math.Max(0, p[0]));
                break;
            case 'K':
                EraseInLine(Math.Max(0, p[0]));
                break;
            case 'L':
                InsertLines(p.Get(0, 1));
                break;
            case 'M':
                DeleteLines(p.Get(0, 1));
                break;
            case 'P':
                DeleteChars(p.Get(0, 1));
                break;
            case 'S':
                ScrollUp(p.Get(0, 1));
                break;
            case 'T':
                ScrollDown(p.Get(0, 1));
                break;
            case 'X':
                GetScreenLine(_cursorY).Clear(_cursorX, _cursorX + p.Get(0, 1), _style);
                _pendingWrap = false;
                break;
            case 'Z':
                TabBackward(p.Get(0, 1));
                break;
            case 'b':
                RepeatLast(p.Get(0, 1));
                break;
            case 'c':
                Send("\x1b[?62;22c"); // DA1: VT220 with ANSI color.
                break;
            case 'd':
                SetCursorY(p.Get(0, 1) - 1);
                break;
            case 'g':
                ClearTabStops(Math.Max(0, p[0]));
                break;
            case 'h' or 'l':
                SetAnsiModes(p, final == 'h');
                break;
            case 'm':
                SelectGraphicRendition(p);
                break;
            case 'n':
                DeviceStatusReport(p.Get(0, 0));
                break;
            case 'r':
                SetScrollRegion(p.Get(0, 1) - 1, p.Get(1, Rows) - 1);
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 't':
                if (p[0] == 18)
                    Send($"\x1b[8;{Rows};{Columns}t");
                break;
        }
    }

    void IVtHandler.OscDispatch(string data)
    {
        var separator = data.IndexOf(';');
        if (separator <= 0 || !int.TryParse(data.AsSpan(0, separator), out var command))
            return;

        var argument = data[(separator + 1)..];
        switch (command)
        {
            case 0 or 2:
                Title = argument;
                TitleChanged?.Invoke(this, argument);
                break;
            case 7 when Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme == "file":
                WorkingDirectoryChanged?.Invoke(this, Uri.UnescapeDataString(uri.AbsolutePath));
                break;
            case 8:
                // OSC 8 ; params ; URI starts a hyperlink, an empty URI ends it.
                var uriStart = argument.IndexOf(';');
                _activeLink = uriStart < 0 ? 0 : LinkIdFor(argument[(uriStart + 1)..]);
                break;
            case 1337:
                InlineImage(argument);
                break;
        }
    }

    private int LinkIdFor(string uri)
    {
        if (uri.Length == 0 || uri.Length > 2048)
            return 0;
        if (_linkIds.TryGetValue(uri, out var id))
            return id;

        // Long sessions could print endless distinct links; start over rather than grow without bound.
        if (_links.Count >= MaxLinks)
        {
            _links.Clear();
            _linkIds.Clear();
        }

        id = _links.Count + 1;
        _links[id] = uri;
        _linkIds[uri] = id;
        return id;
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (_cursorY == _scrollBottom)
            ScrollUp(1);
        else if (_cursorY < Rows - 1)
            _cursorY++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (_cursorY == _scrollTop)
            ScrollDown(1);
        else if (_cursorY > 0)
            _cursorY--;
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        var fullScreen = _scrollTop == 0 && _scrollBottom == Rows - 1;

        if (!_altActive && fullScreen)
        {
            for (var i = 0; i < count; i++)
            {
                CompleteLine(GetScreenLine(0));
                var line = _spare ?? new TerminalLine(Columns);
                _spare = null;
                line.Resize(Columns);
                line.Reset(_style);
                var evicted = _main.Push(line);
                if (evicted is not null)
                    _spare = evicted;
            }

            return;
        }

        RotateRegion(_scrollTop, _scrollBottom, count);
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        RotateRegion(_scrollTop, _scrollBottom, -count);
    }

    /// <summary>
    /// Moves rows [top, bottom] up by <paramref name="count"/> (down when negative); rows that wrap
    /// around are cleared. Used for scroll regions and line insert/delete.
    /// </summary>
    private void RotateRegion(int top, int bottom, int count)
    {
        var height = bottom - top + 1;
        if (count == 0 || height <= 0)
            return;

        var n = Math.Min(Math.Abs(count), height);
        var lines = new TerminalLine[height];
        for (var i = 0; i < height; i++)
            lines[i] = GetScreenLine(top + i);

        for (var i = 0; i < height; i++)
        {
            var source = count > 0 ? (i + n) % height : (i - n + height) % height;
            SetScreenLine(top + i, lines[source]);
        }

        var clearFrom = count > 0 ? height - n : 0;
        for (var i = 0; i < n; i++)
            GetScreenLine(top + clearFrom + i).Reset(_style);
    }

    private void InsertLines(int count)
    {
        if (_cursorY < _scrollTop || _cursorY > _scrollBottom)
            return;
        RotateRegion(_cursorY, _scrollBottom, -count);
        _cursorX = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int count)
    {
        if (_cursorY < _scrollTop || _cursorY > _scrollBottom)
            return;
        RotateRegion(_cursorY, _scrollBottom, count);
        _cursorX = 0;
        _pendingWrap = false;
    }

    private void InsertChars(int count)
    {
        var cells = GetScreenLine(_cursorY).Cells;
        count = Math.Min(count, Columns - _cursorX);
        Array.Copy(cells, _cursorX, cells, _cursorX + count, Columns - _cursorX - count);
        GetScreenLine(_cursorY).Clear(_cursorX, _cursorX + count, _style);
        _pendingWrap = false;
    }

    private void DeleteChars(int count)
    {
        var cells = GetScreenLine(_cursorY).Cells;
        count = Math.Min(count, Columns - _cursorX);
        Array.Copy(cells, _cursorX + count, cells, _cursorX, Columns - _cursorX - count);
        GetScreenLine(_cursorY).Clear(Columns - count, Columns, _style);
        _pendingWrap = false;
    }

    private int _lastRune = ' ';

    private void RepeatLast(int count)
    {
        // REP repeats the preceding graphic character; we track it cheaply from the cell left of the cursor.
        var x = _pendingWrap ? _cursorX : _cursorX - 1;
        if (x >= 0)
            _lastRune = GetScreenLine(_cursorY).Cells[x].Rune is var r and > 0 ? r : ' ';
        for (var i = 0; i < Math.Min(count, Columns * Rows); i++)
            ((IVtHandler)this).Print(_lastRune);
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var y = _cursorY + 1; y < Rows; y++)
                    GetScreenLine(y).Reset(_style);
                break;
            case 1:
                EraseInLine(1);
                for (var y = 0; y < _cursorY; y++)
                    GetScreenLine(y).Reset(_style);
                break;
            case 2:
                // Cleared main-screen content will not scroll out any more: complete it now.
                if (!_altActive && LineCompleted is not null)
                {
                    var last = Rows - 1;
                    while (last >= 0 && GetScreenLine(last).GetText().Length == 0)
                        last--;
                    for (var y = 0; y <= last; y++)
                        CompleteLine(GetScreenLine(y));
                }

                for (var y = 0; y < Rows; y++)
                    GetScreenLine(y).Reset(_style);
                ClearImages(0, Rows - 1);
                break;
            case 3:
                if (!_altActive)
                {
                    // Images only in the scrollback go; those on screen stay, renumbered with their lines.
                    var oldTop = ScreenLineNumber(0);
                    _images.RemoveAll(i => !i.OnAlternateScreen && i.Line + i.Rows <= oldTop);
                    var screen = new TerminalLine[Rows];
                    for (var y = 0; y < Rows; y++)
                        screen[y] = GetScreenLine(y);
                    _main.Clear();
                    foreach (var line in screen)
                        _main.Push(line);
                    var shift = ScreenLineNumber(0) - oldTop;
                    for (var i = 0; i < _images.Count; i++)
                    {
                        if (!_images[i].OnAlternateScreen)
                            _images[i] = _images[i].MovedTo(_images[i].Line + shift);
                    }
                }

                break;
        }

        _pendingWrap = false;
    }

    private void EraseInLine(int mode)
    {
        var line = GetScreenLine(_cursorY);
        switch (mode)
        {
            case 0:
                line.Clear(_cursorX, Columns, _style);
                line.IsWrapped = false;
                break;
            case 1:
                line.Clear(0, _cursorX + 1, _style);
                break;
            case 2:
                line.Reset(_style);
                break;
        }

        _pendingWrap = false;
    }

    private void MoveCursor(int dx, int dy)
    {
        _pendingWrap = false;
        _cursorX = Math.Clamp(_cursorX + dx, 0, Columns - 1);

        // Vertical movement stops at the scroll margins when starting inside them.
        var top = _cursorY >= _scrollTop ? _scrollTop : 0;
        var bottom = _cursorY <= _scrollBottom ? _scrollBottom : Rows - 1;
        _cursorY = Math.Clamp(_cursorY + dy, top, bottom);
    }

    private void SetCursor(int x, int y)
    {
        SetCursorX(x);
        SetCursorY(y);
    }

    private void SetCursorX(int x)
    {
        _pendingWrap = false;
        _cursorX = Math.Clamp(x, 0, Columns - 1);
    }

    private void SetCursorY(int y)
    {
        _pendingWrap = false;
        _cursorY = _originMode
            ? Math.Clamp(y + _scrollTop, _scrollTop, _scrollBottom)
            : Math.Clamp(y, 0, Rows - 1);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (top >= bottom)
            return;

        _scrollTop = top;
        _scrollBottom = bottom;
        SetCursor(0, 0);
    }

    private void TabForward(int count)
    {
        for (var i = 0; i < count && _cursorX < Columns - 1; i++)
        {
            do
            {
                _cursorX++;
            }
            while (_cursorX < Columns - 1 && !_tabStops[_cursorX]);
        }

        _pendingWrap = false;
    }

    private void TabBackward(int count)
    {
        for (var i = 0; i < count && _cursorX > 0; i++)
        {
            do
            {
                _cursorX--;
            }
            while (_cursorX > 0 && !_tabStops[_cursorX]);
        }

        _pendingWrap = false;
    }

    private void ClearTabStops(int mode)
    {
        if (mode == 0)
            _tabStops[_cursorX] = false;
        else if (mode == 3)
            Array.Clear(_tabStops);
    }

    private void SaveCursor()
    {
        var saved = new SavedCursor(_cursorX, _cursorY, _style, _originMode, _autoWrap, _pendingWrap, _g0Graphics, _g1Graphics, _shiftOut);
        if (_altActive)
            _savedAlt = saved;
        else
            _savedMain = saved;
    }

    private void RestoreCursor()
    {
        var saved = _altActive ? _savedAlt : _savedMain;
        _cursorX = Math.Clamp(saved.X, 0, Columns - 1);
        _cursorY = Math.Clamp(saved.Y, 0, Rows - 1);
        _style = saved.Style;
        _originMode = saved.OriginMode;
        _autoWrap = saved == default || saved.AutoWrap;
        _pendingWrap = saved.PendingWrap;
        _g0Graphics = saved.G0Graphics;
        _g1Graphics = saved.G1Graphics;
        _shiftOut = saved.ShiftOut;
    }

    private void SetAnsiModes(VtParams p, bool enable)
    {
        for (var i = 0; i < p.Count; i++)
        {
            switch (p[i])
            {
                case 4:
                    _insertMode = enable;
                    break;
                case 20:
                    _lineFeedNewLine = enable;
                    break;
            }
        }
    }

    private void SetPrivateModes(VtParams p, bool enable)
    {
        for (var i = 0; i < p.Count; i++)
        {
            switch (p[i])
            {
                case 1:
                    IsApplicationCursorKeys = enable;
                    break;
                case 5:
                    IsReverseVideo = enable;
                    break;
                case 6:
                    _originMode = enable;
                    SetCursor(0, 0);
                    break;
                case 7:
                    _autoWrap = enable;
                    if (!enable)
                        _pendingWrap = false;
                    break;
                case 12:
                    IsCursorBlinking = enable;
                    break;
                case 25:
                    IsCursorVisible = enable;
                    break;
                case 47 or 1047:
                    SwitchScreen(enable, saveCursor: false, clear: p[i] == 1047);
                    break;
                case 1049:
                    SwitchScreen(enable, saveCursor: true, clear: true);
                    break;
                case 1048:
                    if (enable)
                        SaveCursor();
                    else
                        RestoreCursor();
                    break;
                case 1000:
                    MouseTracking = enable ? MouseTracking.Normal : MouseTracking.None;
                    break;
                case 1002:
                    MouseTracking = enable ? MouseTracking.ButtonEvent : MouseTracking.None;
                    break;
                case 1003:
                    MouseTracking = enable ? MouseTracking.AnyEvent : MouseTracking.None;
                    break;
                case 1004:
                    ReportsFocus = enable;
                    break;
                case 1006:
                    IsSgrMouse = enable;
                    break;
                case 2004:
                    IsBracketedPaste = enable;
                    break;
            }
        }
    }

    private void SwitchScreen(bool alternate, bool saveCursor, bool clear)
    {
        if (alternate == _altActive)
            return;

        if (alternate)
        {
            if (saveCursor)
                SaveCursor();
            _altActive = true;
            _images.RemoveAll(i => i.OnAlternateScreen);
            if (clear)
            {
                foreach (var line in _alt)
                    line.Reset(_style);
            }
        }
        else
        {
            if (clear)
            {
                foreach (var line in _alt)
                    line.Reset(default);
            }

            _altActive = false;
            _images.RemoveAll(i => i.OnAlternateScreen);
            if (saveCursor)
                RestoreCursor();
        }

        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _pendingWrap = false;
        _inputStart = null;
    }

    private void SetCursorStyle(int style)
    {
        CursorShape = style switch
        {
            3 or 4 => CursorShape.Underline,
            5 or 6 => CursorShape.Bar,
            _ => CursorShape.Block,
        };
        IsCursorBlinking = style is 0 or 1 or 3 or 5;
    }

    private void DeviceStatusReport(int request)
    {
        switch (request)
        {
            case 5:
                Send("\x1b[0n");
                break;
            case 6:
                var row = _originMode ? _cursorY - _scrollTop : _cursorY;
                Send($"\x1b[{row + 1};{_cursorX + 1}R");
                break;
        }
    }

    private void SoftReset()
    {
        IsCursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        IsApplicationCursorKeys = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _style = default;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        _savedMain = _savedAlt = default;
    }

    private void FullReset()
    {
        SwitchScreen(false, saveCursor: false, clear: true);
        SoftReset();
        _activeLink = 0;
        _images.Clear();
        IsReverseVideo = false;
        IsBracketedPaste = false;
        MouseTracking = MouseTracking.None;
        IsSgrMouse = false;
        ReportsFocus = false;
        CursorShape = CursorShape.Block;
        IsCursorBlinking = true;
        _lineFeedNewLine = false;
        _tabStops = CreateTabStops(Columns);
        EraseInDisplay(2);
        EraseInDisplay(3);
        _cursorX = _cursorY = 0;
        _pendingWrap = false;
    }

    private void SelectGraphicRendition(VtParams p)
    {
        if (p.Count == 0)
        {
            _style = default;
            return;
        }

        for (var i = 0; i < p.Count; i++)
        {
            var code = Math.Max(0, p[i]);
            switch (code)
            {
                case 0:
                    _style = default;
                    break;
                case 1:
                    _style = _style.With(CellFlags.Bold);
                    break;
                case 2:
                    _style = _style.With(CellFlags.Dim);
                    break;
                case 3:
                    _style = _style.With(CellFlags.Italic);
                    break;
                case 4:
                    // 4:0 turns underline off; other sub-styles (curly, dotted…) render as plain underline.
                    var off = p.IsSubParameter(i + 1) && p[i + 1] == 0;
                    _style = off ? _style.With(CellFlags.None, CellFlags.Underline) : _style.With(CellFlags.Underline);
                    while (p.IsSubParameter(i + 1))
                        i++;
                    break;
                case 5 or 6:
                    _style = _style.With(CellFlags.Blink);
                    break;
                case 7:
                    _style = _style.With(CellFlags.Inverse);
                    break;
                case 8:
                    _style = _style.With(CellFlags.Hidden);
                    break;
                case 9:
                    _style = _style.With(CellFlags.Strikethrough);
                    break;
                case 21:
                    _style = _style.With(CellFlags.Underline);
                    break;
                case 22:
                    _style = _style.With(CellFlags.None, CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _style = _style.With(CellFlags.None, CellFlags.Italic);
                    break;
                case 24:
                    _style = _style.With(CellFlags.None, CellFlags.Underline);
                    break;
                case 25:
                    _style = _style.With(CellFlags.None, CellFlags.Blink);
                    break;
                case 27:
                    _style = _style.With(CellFlags.None, CellFlags.Inverse);
                    break;
                case 28:
                    _style = _style.With(CellFlags.None, CellFlags.Hidden);
                    break;
                case 29:
                    _style = _style.With(CellFlags.None, CellFlags.Strikethrough);
                    break;
                case >= 30 and <= 37:
                    _style = _style with { Foreground = TerminalColor.Indexed(code - 30) };
                    break;
                case 38:
                    if (ParseExtendedColor(p, ref i) is { } fg)
                        _style = _style with { Foreground = fg };
                    break;
                case 39:
                    _style = _style with { Foreground = TerminalColor.Default };
                    break;
                case >= 40 and <= 47:
                    _style = _style with { Background = TerminalColor.Indexed(code - 40) };
                    break;
                case 48:
                    if (ParseExtendedColor(p, ref i) is { } bg)
                        _style = _style with { Background = bg };
                    break;
                case 49:
                    _style = _style with { Background = TerminalColor.Default };
                    break;
                case >= 90 and <= 97:
                    _style = _style with { Foreground = TerminalColor.Indexed(code - 90 + 8) };
                    break;
                case >= 100 and <= 107:
                    _style = _style with { Background = TerminalColor.Indexed(code - 100 + 8) };
                    break;
            }
        }
    }

    /// <summary>Parses "38;5;n", "38;2;r;g;b" and their colon forms ("38:2::r:g:b").</summary>
    private static TerminalColor? ParseExtendedColor(VtParams p, ref int i)
    {
        var colon = p.IsSubParameter(i + 1);
        var mode = p[i + 1];

        if (mode == 5)
        {
            var index = p[i + 2];
            i += 2;
            return index is >= 0 and <= 255 ? TerminalColor.Indexed(index) : null;
        }

        if (mode == 2)
        {
            // Colon form may include an (often empty) color-space id before r:g:b.
            var start = i + 2;
            if (colon)
            {
                var subCount = 0;
                while (p.IsSubParameter(i + 1 + subCount))
                    subCount++;
                if (subCount >= 5)
                    start = i + 3;
                i += subCount;
            }
            else
            {
                i += 4;
            }

            var r = p[start];
            var g = p[start + 1];
            var b = p[start + 2];
            return r >= 0 && g >= 0 && b >= 0
                ? TerminalColor.Rgb((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255))
                : null;
        }

        return null;
    }
}
