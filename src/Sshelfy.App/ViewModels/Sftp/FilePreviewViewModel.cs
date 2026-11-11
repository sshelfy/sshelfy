using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Sftp;

/// <summary>
/// The preview pane of the file browser: the selected file's text or picture, and for text files
/// an optional live view that appends what the file grows by (like tail -f).
/// </summary>
public sealed partial class FilePreviewViewModel : ObservableObject, IDisposable
{
    private const int MaxShownChars = 1024 * 1024;
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(2);

    private readonly Func<ISftpSession?> _session;
    private CancellationTokenSource? _loadCts;
    private DispatcherTimer? _followTimer;
    private PreviewTail? _tail;
    private RemoteFileEntry? _entry;
    private bool _reading;

    public FilePreviewViewModel(Func<ISftpSession?> session)
    {
        _session = session;
    }

    [ObservableProperty]
    public partial string? Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsImage), nameof(HasMessage))]
    public partial PreviewKind Kind { get; private set; }

    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>Kept at the end of the text so the view scrolls along while following.</summary>
    [ObservableProperty]
    public partial int CaretIndex { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    /// <summary>Shown above the text when only the end of a large file is loaded.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsFollowing { get; set; }

    public bool IsText => Kind == PreviewKind.Text;

    public bool IsImage => Kind == PreviewKind.Image;

    public bool HasMessage => Kind == PreviewKind.None && Message is not null;

    partial void OnIsFollowingChanged(bool value)
    {
        _followTimer?.Stop();
        _followTimer = null;
        if (!value || !IsText)
            return;

        _followTimer = new DispatcherTimer { Interval = FollowInterval };
        _followTimer.Tick += async (_, _) => await ReadAppendedAsync();
        _followTimer.Start();
    }

    /// <summary>Shows <paramref name="entry"/>, or clears the pane for null (no or several files selected).</summary>
    public async Task ShowAsync(RemoteFileEntry? entry)
    {
        if (entry?.FullPath == _entry?.FullPath && entry?.Modified == _entry?.Modified && entry is not null)
            return;

        _loadCts?.Cancel();
        IsFollowing = false;
        _entry = entry;
        _tail = null;
        Clear();
        if (entry is null || _session() is not { } session)
            return;

        Name = entry.Name;
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var content = await FilePreview.LoadAsync(session, entry, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Apply(entry, content, session);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SftpOperationException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            if (_loadCts == cts)
                IsLoading = false;
        }
    }

    private void Apply(RemoteFileEntry entry, PreviewContent content, ISftpSession session)
    {
        Kind = content.Kind;
        Message = content.Message;
        switch (content.Kind)
        {
            case PreviewKind.Text:
                SetText(content.Text ?? string.Empty);
                Note = content.IsPartial ? $"Showing the last {FilePreview.MaxTextBytes / 1024} KB" : null;
                _tail = new PreviewTail(session, entry.FullPath, content.EndOffset);
                break;
            case PreviewKind.Image:
                try
                {
                    Image = new Bitmap(new MemoryStream(content.Image!));
                }
                catch (Exception)
                {
                    Kind = PreviewKind.None;
                    Message = "This picture cannot be shown.";
                }

                break;
        }
    }

    private async Task ReadAppendedAsync()
    {
        if (_reading || _tail is not { } tail || _entry is not { } entry)
            return;

        _reading = true;
        try
        {
            var added = await tail.ReadAsync();
            if (tail != _tail)
                return; // another file was selected while reading

            if (added is null)
            {
                // Rotated or truncated: start over from the new content.
                var following = IsFollowing;
                _entry = null;
                await ShowAsync(entry with { Modified = DateTimeOffset.MinValue });
                IsFollowing = following;
            }
            else if (added.Length > 0)
            {
                var text = Text + added;
                SetText(text.Length > MaxShownChars ? text[^MaxShownChars..] : text);
            }
        }
        catch (SftpOperationException ex)
        {
            if (tail == _tail)
            {
                IsFollowing = false;
                Note = $"Stopped following: {ex.Message}"; // the text stays, so the note is what explains it
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private void SetText(string text)
    {
        Text = text;
        CaretIndex = text.Length;
    }

    private void Clear()
    {
        Name = null;
        Kind = PreviewKind.None;
        Message = null;
        Note = null;
        SetText(string.Empty);
        Image?.Dispose();
        Image = null;
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        IsFollowing = false;
        Image?.Dispose();
    }
}
