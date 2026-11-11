using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Terminal;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Core.Tests;

public sealed class SessionRecordingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sshelfy-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SessionLogStore Store() => new(new StoragePaths(_dir), new TestSettings(), new InMemoryVault());

    [Fact]
    public async Task Plain_text_contains_scrolled_and_remaining_lines_without_escape_codes()
    {
        var terminal = new TerminalEmulator(20, 3);
        var sink = new MemorySink();
        var recorder = new SessionRecorder(terminal, sink, SessionLogFormat.PlainText, timestamps: false, maxBytes: 0, "test");

        terminal.Feed("\x1b[1;32mone\x1b[0m\r\ntwo\r\nthree\r\nfour\r\n$ ");
        terminal.Feed("progress 10%\r$ progress 99%\x1b[K\r\n");   // redrawn progress keeps only the final state
        await recorder.DisposeAsync();

        var lines = sink.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToList();
        Assert.Equal(["one", "two", "three", "four", "$ progress 99%"], lines);
        Assert.True(!sink.Text.Contains('\u001b'), sink.Text.Replace('\u001b', '␛'));
    }

    [Fact]
    public async Task Soft_wrapped_rows_become_one_line_and_timestamps_are_added()
    {
        var terminal = new TerminalEmulator(5, 2);
        var sink = new MemorySink();
        var recorder = new SessionRecorder(terminal, sink, SessionLogFormat.PlainText, timestamps: true, maxBytes: 0, "test");

        terminal.Feed("abcdefghij\r\nx\r\ny\r\n");
        await recorder.DisposeAsync();

        Assert.Matches(@"\[\d\d:\d\d:\d\d\] abcdefghij\n", sink.Text);
    }

    [Fact]
    public async Task Asciicast_records_header_and_output_events()
    {
        var terminal = new TerminalEmulator(80, 24);
        var sink = new MemorySink();
        var recorder = new SessionRecorder(terminal, sink, SessionLogFormat.Asciicast, false, 0, "web-01");

        var bytes = Encoding.UTF8.GetBytes("zażółć\x1b[0m");
        recorder.OnOutput(bytes.AsSpan(0, 3));   // split inside a UTF-8 sequence
        recorder.OnOutput(bytes.AsSpan(3));
        recorder.OnResize(100, 30);
        await recorder.DisposeAsync();

        var lines = sink.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(80, header.RootElement.GetProperty("width").GetInt32());

        var output = string.Concat(lines.Skip(1)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e[1].GetString() == "o")
            .Select(e => e[2].GetString()));
        Assert.Equal("zażółć\x1b[0m", output);
        Assert.Contains(lines, l => l.Contains("\"r\", \"100x30\""));
    }

    [Fact]
    public async Task Recording_stops_at_the_size_limit()
    {
        var terminal = new TerminalEmulator(80, 2);
        var sink = new MemorySink();
        var recorder = new SessionRecorder(terminal, sink, SessionLogFormat.PlainText, false, maxBytes: 200, "test");
        var limit = false;
        recorder.LimitReached += (_, _) => limit = true;

        for (var i = 0; i < 100; i++)
            terminal.Feed($"line {i:000} with some padding\r\n");
        await recorder.DisposeAsync();

        Assert.True(limit);
        Assert.True(recorder.ReachedLimit);
        Assert.True(sink.Text.Length < 400);
    }

    [Fact]
    public async Task Encrypted_log_round_trips_and_is_not_readable_on_disk()
    {
        var store = Store();
        var sink = await store.CreateAsync("web-01", SessionLogFormat.PlainText, encrypt: true);
        sink.Write("secret output line 1\n");
        sink.Write("secret output line 2\n");
        await sink.DisposeAsync();

        var raw = await File.ReadAllBytesAsync(sink.FilePath);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(raw));
        Assert.EndsWith(".log.enc", sink.FilePath);

        var info = Assert.Single(store.List());
        Assert.True(info.IsEncrypted);
        Assert.Equal("web-01", info.HostName);
        Assert.Equal("secret output line 1\nsecret output line 2\n", await store.ReadAllTextAsync(sink.FilePath));
    }

    [Fact]
    public async Task Tampered_encrypted_log_is_rejected()
    {
        var store = Store();
        var sink = await store.CreateAsync("web-01", SessionLogFormat.PlainText, encrypt: true);
        sink.Write("data\n");
        await sink.DisposeAsync();

        var bytes = await File.ReadAllBytesAsync(sink.FilePath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(sink.FilePath, bytes);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAllTextAsync(sink.FilePath));
    }

    [Fact]
    public async Task Plain_log_is_listed_with_host_and_format()
    {
        var store = Store();
        var sink = await store.CreateAsync("db primary/1", SessionLogFormat.Asciicast, encrypt: false);
        sink.Write("{}\n");
        await sink.DisposeAsync();

        var info = Assert.Single(store.List());
        Assert.Equal(SessionLogFormat.Asciicast, info.Format);
        Assert.Equal("db-primary-1", info.HostName);
        Assert.False(info.IsEncrypted);
    }

    private sealed class MemorySink : ISessionLogSink
    {
        private readonly StringBuilder _text = new();

        public string FilePath => "memory";

        public long Length => _text.Length;

        public string Text => _text.ToString();

        public void Write(string text)
        {
            lock (_text)
                _text.Append(text);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class TestSettings : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Update(Func<AppSettings, AppSettings> mutate)
    {
        Current = mutate(Current);
        Changed?.Invoke(this, Current);
    }

    public Task FlushAsync() => Task.CompletedTask;
}
