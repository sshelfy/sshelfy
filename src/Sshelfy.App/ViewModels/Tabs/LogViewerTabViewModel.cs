using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Tabs;

public sealed record PlaybackSpeed(double Factor, string Name);

/// <summary>Read-only view of a session recording: text, or an asciicast replayed in a terminal.</summary>
public sealed partial class LogViewerTabViewModel : TabViewModel
{
    private const double MaxPause = 2.0;

    private readonly ISessionLogStore _store;
    private readonly IFilePickerService _files;
    private readonly IShellIntegration _shell;
    private readonly StatusBarViewModel _status;
    private readonly AsciicastRecording? _recording;
    private readonly IReadOnlyList<double> _times = [];
    private readonly string _content;
    private CancellationTokenSource? _playback;
    private int _nextEvent;
    private double _clock;

    public LogViewerTabViewModel(SessionLogInfo info, string content, ISessionLogStore store, IFilePickerService files, IShellIntegration shell, StatusBarViewModel status)
    {
        Info = info;
        _content = content;
        _store = store;
        _files = files;
        _shell = shell;
        _status = status;

        Title = $"{info.HostName} · {info.Started.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
        Subtitle = info.FilePath;

        if (info.Format == SessionLogFormat.Asciicast)
        {
            _recording = AsciicastParser.Parse(content);
            _times = _recording.CompressedTimes(MaxPause);
            Terminal = new TerminalEmulator(Math.Max(20, _recording.Width), Math.Max(5, _recording.Height), 20_000);
            Duration = _times.Count == 0 ? 0 : _times[^1];
        }
        else
        {
            Text = content;
        }

        SelectedSpeed = Speeds[0];
    }

    public SessionLogInfo Info { get; }

    public override AppIcon Icon => AppIcon.ScrollText;

    public bool IsReplay => _recording is not null;

    public bool IsText => _recording is null;

    public string? Text { get; }

    public TerminalEmulator? Terminal { get; }

    public double Duration { get; }

    public IReadOnlyList<PlaybackSpeed> Speeds { get; } =
    [
        new(1, "1×"),
        new(2, "2×"),
        new(4, "4×"),
        new(8, "8×"),
    ];

    [ObservableProperty]
    public partial PlaybackSpeed SelectedSpeed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial double Position { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    public string PositionText => $"{Format(Position)} / {Format(Duration)}";

    public override string? StatusDetail =>
        $"{(Info.Format == SessionLogFormat.Asciicast ? "Replay" : "Text")}{(Info.IsEncrypted ? "  ·  encrypted" : string.Empty)}  ·  {ByteSize.Format(Info.SizeBytes)}";

    [RelayCommand]
    private async Task PlayPause()
    {
        if (_recording is null)
            return;

        if (IsPlaying)
        {
            Pause();
            return;
        }

        if (_nextEvent >= _recording.Events.Count)
            Restart();

        await PlayAsync();
    }

    [RelayCommand]
    private void Restart()
    {
        Pause();
        Terminal?.Feed("\x1bc");
        _nextEvent = 0;
        _clock = 0;
        Position = 0;
    }

    [RelayCommand]
    private void SkipToEnd()
    {
        if (_recording is null)
            return;
        Pause();
        while (_nextEvent < _recording.Events.Count)
            Apply(_recording.Events[_nextEvent++]);
        _clock = Duration;
        Position = Duration;
    }

    [RelayCommand]
    private async Task Export()
    {
        var extension = Info.Format == SessionLogFormat.Asciicast ? ".cast" : ".log";
        var suggested = Path.GetFileName(Info.FilePath).Replace(".enc", string.Empty);
        if (!suggested.EndsWith(extension, StringComparison.Ordinal))
            suggested += extension;

        var path = await _files.SaveFileAsync(Info.IsEncrypted ? "Export decrypted recording" : "Export recording", suggested);
        if (path is null)
            return;

        await File.WriteAllTextAsync(path, _content);
        _status.Set($"Exported to {path}", StatusLevel.Ok);
    }

    [RelayCommand]
    private void Reveal() => _shell.Reveal(Info.FilePath);

    public override ValueTask DisposeAsync()
    {
        Pause();
        return ValueTask.CompletedTask;
    }

    private async Task PlayAsync()
    {
        if (_recording is null)
            return;

        _playback = new CancellationTokenSource();
        var token = _playback.Token;
        IsPlaying = true;
        try
        {
            while (_nextEvent < _recording.Events.Count && !token.IsCancellationRequested)
            {
                var due = _times[_nextEvent];
                var wait = (due - _clock) / SelectedSpeed.Factor;
                if (wait > 0.005)
                    await Task.Delay(TimeSpan.FromSeconds(wait), token);
                _clock = due;

                // Everything due within the next 10 ms goes in one batch, so fast output stays smooth.
                while (_nextEvent < _recording.Events.Count && _times[_nextEvent] <= _clock + 0.01)
                    Apply(_recording.Events[_nextEvent++]);

                Position = _clock;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsPlaying = false;
        }
    }

    private void Pause()
    {
        _playback?.Cancel();
        _playback = null;
        IsPlaying = false;
    }

    private void Apply(AsciicastEvent e)
    {
        if (Terminal is null)
            return;

        if (e.Type == 'o')
        {
            Terminal.Feed(e.Data);
        }
        else if (e.Type == 'r')
        {
            var parts = e.Data.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var cols) && int.TryParse(parts[1], out var rows))
                Terminal.Resize(cols, rows);
        }
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
