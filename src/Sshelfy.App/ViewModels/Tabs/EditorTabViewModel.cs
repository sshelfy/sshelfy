using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Tabs;

/// <summary>Plain-text editor for a remote file, saved back over SFTP.</summary>
public sealed partial class EditorTabViewModel : TabViewModel
{
    private readonly SftpTabViewModel _owner;
    private readonly IDialogService _dialogs;
    private readonly StatusBarViewModel _status;
    private readonly bool _usesCrLf;
    private readonly bool _hasBom;
    private string _savedText;

    public EditorTabViewModel(SftpTabViewModel owner, ISftpSession session, string remotePath, byte[] content, IDialogService dialogs, StatusBarViewModel status)
    {
        _owner = owner;
        Session = session;
        RemotePath = remotePath;
        _dialogs = dialogs;
        _status = status;
        owner.AcquireSession();

        _hasBom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        var text = Encoding.UTF8.GetString(content, _hasBom ? 3 : 0, content.Length - (_hasBom ? 3 : 0));
        _usesCrLf = text.Contains("\r\n");
        _savedText = text.Replace("\r\n", "\n");
        Text = _savedText;

        Title = Sshelfy.Core.Sftp.RemotePath.Name(remotePath);
        Subtitle = $"{owner.Host.DisplayName}:{remotePath}";
    }

    public ISftpSession Session { get; }

    public string RemotePath { get; }

    public override AppIcon Icon => AppIcon.FileText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(StatusDetail))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    public bool IsDirty => Text != _savedText;

    public override string? StatusDetail =>
        $"{Text.Count(c => c == '\n') + 1} lines  ·  UTF-8{(_usesCrLf ? "  ·  CRLF" : "  ·  LF")}{(IsDirty ? "  ·  modified" : string.Empty)}";

    partial void OnTextChanged(string value)
    {
        var name = Sshelfy.Core.Sftp.RemotePath.Name(RemotePath);
        Title = IsDirty ? name + " •" : name;
    }

    private bool CanSave => IsDirty && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        IsSaving = true;
        try
        {
            var text = _usesCrLf ? Text.Replace("\n", "\r\n") : Text;
            var bytes = Encoding.UTF8.GetBytes(text);
            using var stream = new MemoryStream();
            if (_hasBom)
                stream.Write([0xEF, 0xBB, 0xBF]);
            stream.Write(bytes);
            stream.Position = 0;

            await Session.UploadAsync(stream, RemotePath, null);
            _savedText = Text;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(StatusDetail));
            OnTextChanged(Text);
            _status.Set($"Saved {RemotePath}", StatusLevel.Ok);
            await _owner.RefreshIfShowingAsync(Sshelfy.Core.Sftp.RemotePath.Parent(RemotePath));
        }
        catch (SftpOperationException ex)
        {
            _status.Set($"Could not save: {ex.Message}", StatusLevel.Error);
        }
        finally
        {
            IsSaving = false;
        }
    }

    public override async Task<bool> ConfirmCloseAsync() =>
        !IsDirty || await _dialogs.ConfirmAsync("Unsaved changes", $"Close \"{Sshelfy.Core.Sftp.RemotePath.Name(RemotePath)}\" without saving?", "Discard changes", isDestructive: true);

    public override ValueTask DisposeAsync()
    {
        _owner.ReleaseSession();
        return ValueTask.CompletedTask;
    }

    /// <summary>NUL bytes in the first 8 KB are a reliable sign of binary content.</summary>
    public static bool LooksBinary(byte[] content) => content.AsSpan(0, Math.Min(content.Length, 8192)).IndexOf((byte)0) >= 0;
}
