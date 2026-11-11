namespace Sshelfy.Core.Terminal;

/// <summary>
/// Fixed-capacity ring of lines: the newest lines are the visible screen, older ones form the
/// scrollback. When full, pushing a line evicts (and recycles) the oldest one.
/// </summary>
internal sealed class LineBuffer
{
    private TerminalLine[] _lines;
    private int _start;

    public LineBuffer(int capacity)
    {
        _lines = new TerminalLine[Math.Max(1, capacity)];
    }

    public int Count { get; private set; }

    public int Capacity => _lines.Length;

    /// <summary>Total number of lines evicted since creation; makes line numbers stable for selections.</summary>
    public long Evicted { get; private set; }

    public TerminalLine this[int index]
    {
        get => _lines[(_start + index) % _lines.Length];
        set => _lines[(_start + index) % _lines.Length] = value;
    }

    /// <summary>Appends a line; returns the evicted line (for reuse) when the buffer was full.</summary>
    public TerminalLine? Push(TerminalLine line)
    {
        if (Count < _lines.Length)
        {
            _lines[(_start + Count) % _lines.Length] = line;
            Count++;
            return null;
        }

        var evicted = _lines[_start];
        _lines[_start] = line;
        _start = (_start + 1) % _lines.Length;
        Evicted++;
        return evicted;
    }

    /// <summary>Removes the newest line.</summary>
    public TerminalLine PopLast()
    {
        var line = this[Count - 1];
        Count--;
        return line;
    }

    public void Clear()
    {
        Array.Clear(_lines);
        _start = 0;
        Count = 0;
    }

    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(1, capacity);
        if (capacity == _lines.Length)
            return;

        var keep = Math.Min(Count, capacity);
        var lines = new TerminalLine[capacity];
        for (var i = 0; i < keep; i++)
            lines[i] = this[Count - keep + i];
        Evicted += Count - keep;
        _lines = lines;
        _start = 0;
        Count = keep;
    }
}
