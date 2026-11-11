using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sshelfy.Core.Terminal;

public enum SessionLogFormat
{
    /// <summary>Readable text: lines as they appeared on screen, control sequences removed.</summary>
    PlainText,

    /// <summary>asciicast v2: the raw output stream with timing, for replay.</summary>
    Asciicast,
}

/// <summary>Where a recording goes; implementations write (and possibly encrypt) a file.</summary>
public interface ISessionLogSink : IAsyncDisposable
{
    string FilePath { get; }

    /// <summary>Total bytes written so far (before encryption).</summary>
    long Length { get; }

    /// <summary>Queues text; must not block the caller.</summary>
    void Write(string text);
}

/// <summary>
/// Records a terminal session into an <see cref="ISessionLogSink"/>. Plain text is taken from the
/// emulator's completed lines (soft-wrapped rows are joined); asciicast records the raw output.
/// </summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private readonly TerminalEmulator _terminal;
    private readonly ISessionLogSink _sink;
    private readonly SessionLogFormat _format;
    private readonly bool _timestamps;
    private readonly long _maxBytes;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _pending = new();
    private readonly Lock _lock = new();
    private bool _stopped;

    public SessionRecorder(TerminalEmulator terminal, ISessionLogSink sink, SessionLogFormat format, bool timestamps, long maxBytes, string title)
    {
        _terminal = terminal;
        _sink = sink;
        _format = format;
        _timestamps = timestamps;
        _maxBytes = maxBytes <= 0 ? long.MaxValue : maxBytes;

        if (format == SessionLogFormat.Asciicast)
        {
            var header = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["width"] = terminal.Columns,
                ["height"] = terminal.Rows,
                ["timestamp"] = _started.ToUnixTimeSeconds(),
                ["title"] = title,
                ["env"] = new Dictionary<string, string> { ["TERM"] = "xterm-256color" },
            };
            _sink.Write(JsonSerializer.Serialize(header) + "\n");
        }
        else
        {
            _sink.Write($"# sshelfy session log: {title}, started {_started:yyyy-MM-dd HH:mm:ss zzz}\n");
            terminal.LineCompleted += OnLineCompleted;
        }
    }

    public string FilePath => _sink.FilePath;

    public DateTimeOffset Started => _started;

    /// <summary>Set when the size limit was reached and recording stopped by itself.</summary>
    public bool ReachedLimit { get; private set; }

    public event EventHandler? LimitReached;

    /// <summary>Raw bytes received from the server (only used by the asciicast format).</summary>
    public void OnOutput(ReadOnlySpan<byte> data)
    {
        if (_format != SessionLogFormat.Asciicast || _stopped)
            return;

        string text;
        lock (_lock)
        {
            var chars = new char[_decoder.GetCharCount(data, flush: false)];
            var count = _decoder.GetChars(data, chars, flush: false);
            text = new string(chars, 0, count);
        }

        if (text.Length == 0)
            return;

        var elapsed = (DateTimeOffset.Now - _started).TotalSeconds;
        Emit($"[{elapsed.ToString("0.000000", CultureInfo.InvariantCulture)}, \"o\", {JsonSerializer.Serialize(text)}]\n");
    }

    /// <summary>Terminal size changes are part of an asciicast recording.</summary>
    public void OnResize(int columns, int rows)
    {
        if (_format != SessionLogFormat.Asciicast || _stopped)
            return;
        var elapsed = (DateTimeOffset.Now - _started).TotalSeconds;
        Emit($"[{elapsed.ToString("0.000000", CultureInfo.InvariantCulture)}, \"r\", \"{columns}x{rows}\"]\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (_format == SessionLogFormat.PlainText)
        {
            _terminal.LineCompleted -= OnLineCompleted;

            // What is still on screen never scrolled out; write it so the log ends where the session did.
            if (!_stopped)
            {
                foreach (var (text, wrapped) in _terminal.GetPendingLines())
                    OnLineCompleted(text, wrapped);
            }

            lock (_lock)
            {
                if (_pending.Length > 0 && !_stopped)
                    WriteLine(_pending.ToString());
                _pending.Clear();
            }

            if (!_stopped)
                _sink.Write($"# Session log ended {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n");
        }

        _stopped = true;
        await _sink.DisposeAsync().ConfigureAwait(false);
    }

    private void OnLineCompleted(string text, bool wrapsToNext)
    {
        if (_stopped)
            return;

        lock (_lock)
        {
            _pending.Append(text);
            if (wrapsToNext)
                return;
            var line = _pending.ToString();
            _pending.Clear();
            WriteLine(line);
        }
    }

    private void WriteLine(string line)
    {
        var prefix = _timestamps ? $"[{DateTime.Now:HH:mm:ss}] " : string.Empty;
        Emit(prefix + line + "\n");
    }

    private void Emit(string text)
    {
        if (_stopped)
            return;

        if (_sink.Length + text.Length > _maxBytes)
        {
            _stopped = true;
            ReachedLimit = true;
            _sink.Write(_format == SessionLogFormat.PlainText
                ? "# Size limit reached; recording stopped.\n"
                : string.Empty);
            LimitReached?.Invoke(this, EventArgs.Empty);
            return;
        }

        _sink.Write(text);
    }
}
