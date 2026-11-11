using System.Text;

namespace Sshelfy.Core.Terminal;

public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

public enum TerminalMouseAction
{
    Press,
    Release,
    Move,
    WheelUp,
    WheelDown,
}

/// <summary>Keyboard input as the user gave it, before encoding; lets another terminal replay it (broadcast).</summary>
public abstract record TerminalInput;

public sealed record TextInput(string Text) : TerminalInput;

public sealed record KeyInput(TerminalKey Key, TerminalModifiers Modifiers) : TerminalInput;

public sealed record ControlInput(char Key, bool Alt) : TerminalInput;

public sealed record PasteInput(string Text) : TerminalInput;

/// <summary>User input encoding (keys, text, paste, mouse) and reading text back from the screen.</summary>
public sealed partial class TerminalEmulator
{
    /// <summary>Raised after keyboard input or a paste was sent (not for mouse, focus or protocol replies).</summary>
    public event Action<TerminalInput>? InputSent;

    /// <summary>Sends input that came from another terminal, without raising <see cref="InputSent"/> again.</summary>
    public void Replay(TerminalInput input)
    {
        switch (input)
        {
            case TextInput text:
                SendTextCore(text.Text);
                break;
            case KeyInput key:
                SendKeyCore(key.Key, key.Modifiers);
                break;
            case ControlInput control:
                SendControlCore(control.Key, control.Alt);
                break;
            case PasteInput paste:
                PasteCore(paste.Text);
                break;
        }
    }

    /// <summary>Sends typed text (already composed by the platform, including IME output).</summary>
    public void SendText(string text)
    {
        if (SendTextCore(text))
            InputSent?.Invoke(new TextInput(text));
    }

    public void SendKey(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None)
    {
        SendKeyCore(key, modifiers);
        InputSent?.Invoke(new KeyInput(key, modifiers));
    }

    /// <summary>Ctrl+key: sends the matching C0 control code (Ctrl+C → 0x03, Ctrl+[ → ESC…).</summary>
    public bool SendControl(char key, bool alt = false)
    {
        if (!SendControlCore(key, alt))
            return false;
        InputSent?.Invoke(new ControlInput(key, alt));
        return true;
    }

    /// <summary>Alt+key: ESC prefix.</summary>
    public void SendAlt(string text) => SendText("\x1b" + text);

    public void Paste(string text)
    {
        if (PasteCore(text))
            InputSent?.Invoke(new PasteInput(text));
    }

    private bool SendTextCore(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        lock (SyncRoot)
        {
            MarkInputStart();
        }

        Send(text);
        return true;
    }

    private void SendKeyCore(TerminalKey key, TerminalModifiers modifiers)
    {
        string? sequence;
        lock (SyncRoot)
        {
            if (key == TerminalKey.Enter)
                CaptureSubmittedCommand();
            else if (key is TerminalKey.Up or TerminalKey.Down or TerminalKey.Tab)
                MarkInputStart(); // history recall and completion also produce the command line.

            sequence = EncodeKey(key, modifiers, IsApplicationCursorKeys);
        }

        if (sequence is not null)
            Send(sequence);
    }

    private bool SendControlCore(char key, bool alt)
    {
        var code = char.ToUpperInvariant(key) switch
        {
            >= 'A' and <= 'Z' and var c => (char)(c - '@'),
            '@' or ' ' or '2' => '\0',
            '[' or '3' => '\x1b',
            '\\' or '4' => '\x1c',
            ']' or '5' => '\x1d',
            '^' or '6' => '\x1e',
            '_' or '-' or '/' or '7' => '\x1f',
            '?' or '8' => '\x7f',
            _ => (char?)null,
        };

        if (code is not { } control)
            return false;

        if (control is '\x03' or '\x15')
        {
            lock (SyncRoot)
            {
                _inputStart = null; // Ctrl+C / Ctrl+U abandon the current line.
            }
        }

        Send(alt ? "\x1b" + control : control.ToString());
        return true;
    }

    private bool PasteCore(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // Terminals expect CR for Enter; strip anything that could end bracketed paste early.
        var normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (SyncRoot)
        {
            MarkInputStart();
            bracketed = IsBracketedPaste;
        }

        Send(bracketed ? $"\x1b[200~{normalized.Replace("\x1b[201~", string.Empty)}\x1b[201~" : normalized);
        return true;
    }

    public void SendFocus(bool focused)
    {
        if (ReportsFocus)
            Send(focused ? "\x1b[I" : "\x1b[O");
    }

    /// <summary>
    /// Reports a mouse event to the application if it enabled mouse tracking.
    /// Columns and rows are zero-based screen coordinates; button 0=left, 1=middle, 2=right.
    /// </summary>
    /// <returns>True if the event was sent (the view should not use it for selection).</returns>
    public bool SendMouse(TerminalMouseAction action, int button, int column, int row, TerminalModifiers modifiers)
    {
        var tracking = MouseTracking;
        if (tracking == MouseTracking.None)
            return false;
        if (action == TerminalMouseAction.Move && tracking == MouseTracking.Normal)
            return true;
        if (action == TerminalMouseAction.Move && tracking == MouseTracking.ButtonEvent && button < 0)
            return true;

        var code = action switch
        {
            TerminalMouseAction.WheelUp => 64,
            TerminalMouseAction.WheelDown => 65,
            TerminalMouseAction.Release when !IsSgrMouse => 3,
            _ => Math.Clamp(button, 0, 2),
        };

        if (action == TerminalMouseAction.Move)
            code = (button < 0 ? 3 : code) + 32;
        if ((modifiers & TerminalModifiers.Shift) != 0)
            code += 4;
        if ((modifiers & TerminalModifiers.Alt) != 0)
            code += 8;
        if ((modifiers & TerminalModifiers.Control) != 0)
            code += 16;

        var x = Math.Clamp(column, 0, Columns - 1) + 1;
        var y = Math.Clamp(row, 0, Rows - 1) + 1;

        if (IsSgrMouse)
        {
            Send($"\x1b[<{code};{x};{y}{(action == TerminalMouseAction.Release ? 'm' : 'M')}");
        }
        else if (x < 224 && y < 224)
        {
            Send($"\x1b[M{(char)(code + 32)}{(char)(x + 32)}{(char)(y + 32)}");
        }

        return true;
    }

    /// <summary>
    /// Text between two positions given as stable line numbers (see <see cref="FirstLineNumber"/>).
    /// Soft-wrapped rows are joined without a newline. Caller must hold <see cref="SyncRoot"/>.
    /// </summary>
    public string GetText(long startLine, int startColumn, long endLine, int endColumn)
    {
        if (startLine > endLine || (startLine == endLine && startColumn > endColumn))
            (startLine, startColumn, endLine, endColumn) = (endLine, endColumn, startLine, startColumn);

        var sb = new StringBuilder();
        for (var number = startLine; number <= endLine; number++)
        {
            var index = number - FirstLineNumber;
            if (index < 0 || index >= TotalLines)
                continue;

            var line = GetLine((int)index);
            var from = number == startLine ? startColumn : 0;
            var to = number == endLine ? endColumn : line.Length;
            var isLast = number == endLine;
            sb.Append(line.GetText(from, to, trimEnd: !line.IsWrapped || isLast));
            if (!isLast && !line.IsWrapped)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Column range [start, end) of the word under a position, for double-click selection.</summary>
    public (int Start, int End) GetWordBounds(long lineNumber, int column)
    {
        var index = lineNumber - FirstLineNumber;
        if (index < 0 || index >= TotalLines)
            return (column, column);

        var cells = GetLine((int)index).Cells;
        column = Math.Clamp(column, 0, cells.Length - 1);
        var kind = Classify(cells[column].Rune);

        int start = column, end = column + 1;
        while (start > 0 && Classify(cells[start - 1].Rune) == kind)
            start--;
        while (end < cells.Length && Classify(cells[end].Rune) == kind)
            end++;
        return (start, end);

        // 0 = blank, 1 = word (paths and URLs count as words), 2 = other punctuation.
        static int Classify(int rune) => rune switch
        {
            0 or ' ' => 0,
            > 0x7F => 1,
            '-' or '_' or '.' or '/' or '~' or ':' or '@' or '%' or '+' or '=' or '?' or '&' or '#' => 1,
            _ when char.IsLetterOrDigit((char)rune) => 1,
            _ => 2,
        };
    }

    internal static string? EncodeKey(TerminalKey key, TerminalModifiers mods, bool applicationCursor)
    {
        var m = 1 + ((mods & TerminalModifiers.Shift) != 0 ? 1 : 0) + ((mods & TerminalModifiers.Alt) != 0 ? 2 : 0) +
                ((mods & TerminalModifiers.Control) != 0 ? 4 : 0);
        var alt = (mods & TerminalModifiers.Alt) != 0 ? "\x1b" : string.Empty;

        string Cursor(char final) =>
            m > 1 ? $"\x1b[1;{m}{final}" : applicationCursor ? $"\x1bO{final}" : $"\x1b[{final}";

        string Tilde(int code) => m > 1 ? $"\x1b[{code};{m}~" : $"\x1b[{code}~";

        string Ss3(char final) => m > 1 ? $"\x1b[1;{m}{final}" : $"\x1bO{final}";

        return key switch
        {
            TerminalKey.Enter => alt + "\r",
            TerminalKey.Tab => (mods & TerminalModifiers.Shift) != 0 ? "\x1b[Z" : alt + "\t",
            TerminalKey.Backspace => (mods & TerminalModifiers.Control) != 0 ? alt + "\b" : alt + "\x7f",
            TerminalKey.Escape => alt + "\x1b",
            TerminalKey.Up => Cursor('A'),
            TerminalKey.Down => Cursor('B'),
            TerminalKey.Right => Cursor('C'),
            TerminalKey.Left => Cursor('D'),
            TerminalKey.Home => Cursor('H'),
            TerminalKey.End => Cursor('F'),
            TerminalKey.Insert => Tilde(2),
            TerminalKey.Delete => Tilde(3),
            TerminalKey.PageUp => Tilde(5),
            TerminalKey.PageDown => Tilde(6),
            TerminalKey.F1 => Ss3('P'),
            TerminalKey.F2 => Ss3('Q'),
            TerminalKey.F3 => Ss3('R'),
            TerminalKey.F4 => Ss3('S'),
            TerminalKey.F5 => Tilde(15),
            TerminalKey.F6 => Tilde(17),
            TerminalKey.F7 => Tilde(18),
            TerminalKey.F8 => Tilde(19),
            TerminalKey.F9 => Tilde(20),
            TerminalKey.F10 => Tilde(21),
            TerminalKey.F11 => Tilde(23),
            TerminalKey.F12 => Tilde(24),
            _ => null,
        };
    }

    /// <summary>Remembers where the user started typing the current command line.</summary>
    private void MarkInputStart()
    {
        if (_inputStart is null && !_altActive)
            _inputStart = (FirstLineNumber + ScrollbackCount + _cursorY, _cursorX);
    }

    /// <summary>
    /// On Enter, reads the echoed command line from the screen (from where typing started to the
    /// end of the logical line). Reading the screen instead of the keystrokes captures tab
    /// completion and history recall; unechoed input such as passwords yields nothing.
    /// </summary>
    private void CaptureSubmittedCommand()
    {
        if (_inputStart is not { } start || _altActive)
        {
            _inputStart = null;
            return;
        }

        _inputStart = null;
        var endLine = start.Line;
        while (true)
        {
            var index = endLine - FirstLineNumber;
            if (index < 0 || index >= TotalLines - 1 || !GetLine((int)index).IsWrapped)
                break;
            endLine++;
        }

        // Keep a leading space: shells treat " cmd" as "don't remember this" (ignorespace).
        var text = GetText(start.Line, start.Column, endLine, Columns).TrimEnd();
        if (!string.IsNullOrWhiteSpace(text) && text.Length <= 4096)
            CommandSubmitted?.Invoke(this, text);
    }
}
