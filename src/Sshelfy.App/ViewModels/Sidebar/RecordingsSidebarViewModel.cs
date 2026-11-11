using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Security;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Sidebar;

public sealed class RecordingItemViewModel(SessionLogInfo info, RecordingsSidebarViewModel owner) : ViewModelBase
{
    public SessionLogInfo Info { get; } = info;

    public RecordingsSidebarViewModel Owner { get; } = owner;

    public string HostName => Info.HostName;

    public string Detail => $"{Info.Started:yyyy-MM-dd HH:mm}  ·  {ByteSize.Format(Info.SizeBytes)}";

    public bool IsReplay => Info.Format == SessionLogFormat.Asciicast;

    public bool IsEncrypted => Info.IsEncrypted;
}

/// <summary>Session recordings, newest first, with search by host and (optionally) by content.</summary>
public sealed partial class RecordingsSidebarViewModel : SidebarSectionViewModel
{
    private readonly ISessionLogStore _store;
    private readonly ISecretVault _vault;
    private readonly IVaultGate _vaultGate;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _files;
    private readonly IShellIntegration _shell;
    private readonly WorkspaceViewModel _workspace;
    private readonly StatusBarViewModel _status;
    private CancellationTokenSource? _search;

    public RecordingsSidebarViewModel(
        ISessionLogStore store,
        ISecretVault vault,
        IVaultGate vaultGate,
        IDialogService dialogs,
        IFilePickerService files,
        IShellIntegration shell,
        WorkspaceViewModel workspace,
        StatusBarViewModel status)
    {
        _store = store;
        _vault = vault;
        _vaultGate = vaultGate;
        _dialogs = dialogs;
        _files = files;
        _shell = shell;
        _workspace = workspace;
        _status = status;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsSelected) && IsSelected)
                _ = Refresh();
        };
    }

    public override string Title => "Recordings";

    public override AppIcon Icon => AppIcon.ScrollText;

    public ObservableCollection<RecordingItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Also look inside recordings (encrypted ones only while the vault is unlocked).</summary>
    [ObservableProperty]
    public partial bool SearchContents { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial RecordingItemViewModel? SelectedItem { get; set; }

    public bool IsEmpty => Items.Count == 0 && SearchText.Length == 0;

    public bool HasNoResults => Items.Count == 0 && SearchText.Length > 0 && !IsSearching;

    public string FolderText => _store.Directory;

    partial void OnSearchTextChanged(string value) => _ = Refresh();

    partial void OnSearchContentsChanged(bool value) => _ = Refresh();

    [RelayCommand]
    private async Task Refresh()
    {
        if (_search is not null)
            await _search.CancelAsync();
        _search = new CancellationTokenSource();
        var token = _search.Token;

        var filter = SearchText.Trim();
        var all = _store.List();
        Items.Clear();

        var byName = filter.Length == 0 ? all : all.Where(l => l.HostName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var log in byName)
            Items.Add(new RecordingItemViewModel(log, this));
        RaiseState();

        if (!SearchContents || filter.Length < 3)
            return;

        // Content search runs in the background and appends matches as they are found.
        IsSearching = true;
        try
        {
            foreach (var log in all.Except(byName))
            {
                token.ThrowIfCancellationRequested();
                if (log.IsEncrypted && _vault.State != VaultState.Unlocked)
                    continue;
                if (await ContainsAsync(log, filter, token))
                    Items.Add(new RecordingItemViewModel(log, this));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsSearching = false;
            RaiseState();
        }
    }

    [RelayCommand]
    private async Task Open(RecordingItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;

        var existing = _workspace.Find<LogViewerTabViewModel>(t => t.Info.FilePath == item.Info.FilePath);
        if (existing is not null)
        {
            _workspace.ActiveTab = existing;
            return;
        }

        if (item.IsEncrypted && !await _vaultGate.EnsureUnlockedAsync("This recording is encrypted with a key from the vault."))
            return;

        try
        {
            var content = await _store.ReadAllTextAsync(item.Info.FilePath);
            _workspace.Open(new LogViewerTabViewModel(item.Info, content, _store, _files, _shell, _status));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            _status.Set($"Could not open the recording: {ex.Message}", StatusLevel.Error);
        }
    }

    [RelayCommand]
    private async Task Delete(RecordingItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;
        if (!await _dialogs.ConfirmAsync("Delete recording", $"Delete the recording of {item.HostName} from {item.Info.Started:yyyy-MM-dd HH:mm}?", "Delete", isDestructive: true))
            return;

        if (_workspace.Find<LogViewerTabViewModel>(t => t.Info.FilePath == item.Info.FilePath) is { } open)
            await _workspace.CloseTab(open);
        _store.Delete(item.Info.FilePath);
        await Refresh();
    }

    [RelayCommand]
    private void Reveal(RecordingItemViewModel? item)
    {
        if ((item ?? SelectedItem) is { } target)
            _shell.Reveal(target.Info.FilePath);
    }

    [RelayCommand]
    private void OpenFolder() => _shell.OpenFolder(_store.Directory);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private async Task<bool> ContainsAsync(SessionLogInfo log, string filter, CancellationToken token)
    {
        try
        {
            var text = await _store.ReadAllTextAsync(log.FilePath, token);
            return text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or VaultLockedException)
        {
            return false;
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(FolderText));
    }
}
