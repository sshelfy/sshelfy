using System.Text;

namespace Sshelfy.Core.Terminal;

/// <summary>Receives the actions produced by <see cref="VtParser"/>.</summary>
internal interface IVtHandler
{
    void Print(int rune);

    void Execute(char control);

    void EscDispatch(ReadOnlySpan<char> intermediates, char final);

    void CsiDispatch(VtParams parameters, char privateMarker, ReadOnlySpan<char> intermediates, char final);

    void OscDispatch(string data);

    /// <summary>A complete DCS string: parameters, intermediates, final character and data (e.g. "0;1q#0;2;…" for sixel).</summary>
    void DcsDispatch(string data);
}

/// <summary>
/// Parameters of a control sequence. Values are -1 when omitted. A value whose
/// <see cref="IsSubParameter"/> flag is set was separated from the previous one by ':'.
/// </summary>
internal sealed class VtParams
{
    public const int Max = 32;

    private readonly int[] _values = new int[Max];
    private readonly bool[] _sub = new bool[Max];

    public int Count { get; private set; }

    public int this[int index] => index < Count ? _values[index] : -1;

    public bool IsSubParameter(int index) => index < Count && _sub[index];

    /// <summary>Value at <paramref name="index"/>, or <paramref name="fallback"/> when omitted or zero.</summary>
    public int Get(int index, int fallback)
    {
        var value = this[index];
        return value <= 0 ? fallback : value;
    }

    internal void Reset()
    {
        Count = 0;
    }

    internal void BeginNext(bool sub)
    {
        if (Count >= Max)
            return;
        _values[Count] = -1;
        _sub[Count] = sub;
        Count++;
    }

    internal void AddDigit(int digit)
    {
        if (Count == 0)
            BeginNext(false);
        ref var v = ref _values[Count - 1];
        v = v < 0 ? digit : Math.Min(v * 10 + digit, 99_999);
    }
}

/// <summary>
/// VT500-style escape sequence parser (after Paul Williams' state machine), fed with UTF-8 bytes.
/// DCS strings are collected for <see cref="IVtHandler.DcsDispatch"/> (sixel images); SOS, PM and APC are ignored.
/// </summary>
internal sealed class VtParser
{
    private const int MaxOscLength = 8192;

    /// <summary>Inline images (OSC 1337 File=…, DCS sixel) carry whole files.</summary>
    private const int MaxImageLength = 16 * 1024 * 1024;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsString,
        IgnoreString,
    }

    private readonly IVtHandler _handler;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly VtParams _params = new();
    private readonly StringBuilder _osc = new();
    private readonly StringBuilder _dcs = new();
    private bool _oscIsImage;
    private readonly char[] _intermediates = new char[4];
    private char[] _chars = new char[4096];

    private State _state = State.Ground;
    private int _intermediateCount;
    private char _privateMarker;
    private bool _stringEscape;
    private char _highSurrogate;

    public VtParser(IVtHandler handler)
    {
        _handler = handler;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        var needed = _decoder.GetCharCount(data, flush: false);
        if (needed > _chars.Length)
            _chars = new char[Math.Max(needed, _chars.Length * 2)];

        var count = _decoder.GetChars(data, _chars, flush: false);
        for (var i = 0; i < count; i++)
            Advance(_chars[i]);
    }

    /// <summary>Feeds already-decoded text (used by tests and local echo).</summary>
    public void Feed(string text)
    {
        foreach (var ch in text)
            Advance(ch);
    }

    private void Advance(char ch)
    {
        // Controls that act in every state.
        switch (ch)
        {
            case '\x18' or '\x1A':
                _handler.Execute(ch);
                _state = State.Ground;
                return;
            case '\x1B':
                if (_state is State.OscString or State.DcsString or State.IgnoreString)
                {
                    _stringEscape = true;
                    return;
                }

                EnterEscape();
                return;
        }

        switch (_state)
        {
            case State.Ground:
                if (ch < 0x20 || ch == 0x7F)
                    ExecuteControl(ch);
                else
                    PrintChar(ch);
                break;

            case State.Escape:
                if (ch < 0x20)
                    ExecuteControl(ch);
                else if (ch <= 0x2F)
                {
                    Collect(ch);
                    _state = State.EscapeIntermediate;
                }
                else if (ch == '[')
                    EnterCsi();
                else if (ch == ']')
                {
                    _osc.Clear();
                    _oscIsImage = false;
                    _stringEscape = false;
                    _state = State.OscString;
                }
                else if (ch == 'P')
                {
                    _dcs.Clear();
                    _stringEscape = false;
                    _state = State.DcsString;
                }
                else if (ch is 'X' or '^' or '_')
                {
                    _stringEscape = false;
                    _state = State.IgnoreString;
                }
                else if (ch <= 0x7E)
                {
                    _handler.EscDispatch(_intermediates.AsSpan(0, _intermediateCount), ch);
                    _state = State.Ground;
                }

                break;

            case State.EscapeIntermediate:
                if (ch < 0x20)
                    ExecuteControl(ch);
                else if (ch <= 0x2F)
                    Collect(ch);
                else if (ch <= 0x7E)
                {
                    _handler.EscDispatch(_intermediates.AsSpan(0, _intermediateCount), ch);
                    _state = State.Ground;
                }

                break;

            case State.CsiEntry:
            case State.CsiParam:
                if (ch < 0x20)
                    ExecuteControl(ch);
                else if (ch is >= '0' and <= '9')
                {
                    _params.AddDigit(ch - '0');
                    _state = State.CsiParam;
                }
                else if (ch == ';' || ch == ':')
                {
                    if (_params.Count == 0)
                        _params.BeginNext(false);
                    _params.BeginNext(ch == ':');
                    _state = State.CsiParam;
                }
                else if (ch is >= '<' and <= '?')
                {
                    if (_state == State.CsiEntry)
                    {
                        _privateMarker = ch;
                        _state = State.CsiParam;
                    }
                    else
                    {
                        _state = State.CsiIgnore;
                    }
                }
                else if (ch <= 0x2F)
                {
                    Collect(ch);
                    _state = State.CsiIntermediate;
                }
                else if (ch is >= '@' and <= '~')
                    DispatchCsi(ch);

                break;

            case State.CsiIntermediate:
                if (ch < 0x20)
                    ExecuteControl(ch);
                else if (ch <= 0x2F)
                    Collect(ch);
                else if (ch <= 0x3F)
                    _state = State.CsiIgnore;
                else if (ch <= 0x7E)
                    DispatchCsi(ch);
                break;

            case State.CsiIgnore:
                if (ch < 0x20)
                    ExecuteControl(ch);
                else if (ch is >= '@' and <= '~')
                    _state = State.Ground;
                break;

            case State.OscString:
                if (_stringEscape)
                {
                    // ESC \ (ST) terminates; anything else aborts the string and starts a new escape.
                    _stringEscape = false;
                    _handler.OscDispatch(_osc.ToString());
                    if (ch == '\\')
                    {
                        _state = State.Ground;
                    }
                    else
                    {
                        EnterEscape();
                        Advance(ch);
                    }
                }
                else if (ch == '\x07')
                {
                    _handler.OscDispatch(_osc.ToString());
                    _state = State.Ground;
                }
                else if (ch >= 0x20 && _osc.Length < (_oscIsImage ? MaxImageLength : MaxOscLength))
                {
                    _osc.Append(ch);
                    if (_osc.Length == 5)
                        _oscIsImage = _osc.ToString() == "1337;";
                }

                break;

            case State.DcsString:
                if (_stringEscape)
                {
                    _stringEscape = false;
                    _handler.DcsDispatch(_dcs.ToString());
                    _dcs.Clear();
                    if (ch == '\\')
                        _state = State.Ground;
                    else
                    {
                        EnterEscape();
                        Advance(ch);
                    }
                }
                else if (ch >= 0x20 && _dcs.Length < MaxImageLength)
                {
                    // Controls inside the string (line breaks some encoders add) carry no meaning.
                    _dcs.Append(ch);
                }

                break;

            case State.IgnoreString:
                if (_stringEscape)
                {
                    _stringEscape = false;
                    if (ch == '\\')
                        _state = State.Ground;
                    else
                    {
                        EnterEscape();
                        Advance(ch);
                    }
                }
                else if (ch == '\x07')
                {
                    _state = State.Ground;
                }

                break;
        }
    }

    private void ExecuteControl(char ch)
    {
        if (ch != 0x7F)
            _handler.Execute(ch);
    }

    private void PrintChar(char ch)
    {
        if (char.IsHighSurrogate(ch))
        {
            _highSurrogate = ch;
            return;
        }

        if (char.IsLowSurrogate(ch))
        {
            if (_highSurrogate != 0)
                _handler.Print(char.ConvertToUtf32(_highSurrogate, ch));
            _highSurrogate = '\0';
            return;
        }

        _highSurrogate = '\0';
        _handler.Print(ch);
    }

    private void EnterEscape()
    {
        _state = State.Escape;
        _intermediateCount = 0;
    }

    private void EnterCsi()
    {
        _state = State.CsiEntry;
        _params.Reset();
        _intermediateCount = 0;
        _privateMarker = '\0';
    }

    private void Collect(char ch)
    {
        if (_intermediateCount < _intermediates.Length)
            _intermediates[_intermediateCount++] = ch;
    }

    private void DispatchCsi(char final)
    {
        _handler.CsiDispatch(_params, _privateMarker, _intermediates.AsSpan(0, _intermediateCount), final);
        _state = State.Ground;
    }
}
