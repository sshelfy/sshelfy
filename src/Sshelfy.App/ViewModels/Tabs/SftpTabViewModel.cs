using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Sftp;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Services;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.ViewModels.Tabs;

/// <summary>Remote file browser: folder tree, file list, navigation history and file operations.</summary>
public sealed partial class SftpTabViewModel : TabViewModel
{
    private const long MaxEditableSize = 2 * 1024 * 1024;

    private readonly HostProfile _host;
    private readonly ISftpSessionFactory _sessions;
    private readonly IConnectionResolver _resolver;
    private readonly TransferQueue _transfers;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _files;
    private readonly ISettingsService _settings;
    private readonly IClipboardService _clipboard;
    private readonly WorkspaceViewModel _workspace;
    private readonly StatusBarViewModel _status;

    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private List<RemoteEntryViewModel> _allEntries = [];
    private ISftpSession? _session;
    private int _leases;
    private bool _disposeRequested;
    private CancellationTokenSource? _listCts;
    private readonly Sshelfy.Core.Activity.IActivityLog _activity;
    private readonly ILocalFileSystem _localFiles;
    private string? _password;
    private readonly string? _startPath;
    private TerminalTabViewModel? _followed;
    private readonly IShellIntegration _shell;

    public SftpTabViewModel(
        HostProfile host,
        string? password,
        string? startPath,
        ISftpSessionFactory sessions,
        IConnectionResolver resolver,
        TransferQueue transfers,
        IDialogService dialogs,
        IFilePickerService files,
        ISettingsService settings,
        IClipboardService clipboard,
        WorkspaceViewModel workspace,
        StatusBarViewModel status,
        Sshelfy.Core.Activity.IActivityLog activity,
        ILocalFileSystem localFiles,
        IShellIntegration shell)
    {
        _activity = activity;
        _password = password;
        _startPath = startPath;
        _localFiles = localFiles;
        _shell = shell;
        _host = host;
        _sessions = sessions;
        _resolver = resolver;
        _transfers = transfers;
        _dialogs = dialogs;
        _files = files;
        _settings = settings;
        _clipboard = clipboard;
        _workspace = workspace;
        _status = status;

        Title = host.DisplayName;
        Subtitle = $"SFTP · {host.Endpoint}";
        SelectedEntries.CollectionChanged += (_, _) => OnSelectionChanged();
        _transfers.Finished += OnTransferFinished;
        IsDualPane = settings.Current.SftpDualPane;
        Preview = new FilePreviewViewModel(() => _session);
        IsPreviewOpen = settings.Current.SftpPreview;
        _workspace.PropertyChanged += OnWorkspaceChanged;
        Sort.Changed += (_, _) =>
        {
            _allEntries = SortEntries(_allEntries).ToList();
            ApplyFilter();
        };
    }

    public FileSort Sort { get; } = new();

    private IEnumerable<RemoteEntryViewModel> SortEntries(IEnumerable<RemoteEntryViewModel> entries) =>
        Sort.Apply(entries, e => e.IsDirectory, e => e.Name, e => e.Entry.Size, e => e.Entry.Modified);

    // ---------------------------------------------------------------- follow the terminal

    /// <summary>Keep showing the folder the shell of a terminal on the same host is in.</summary>
    [ObservableProperty]
    public partial bool FollowTerminal { get; set; }

    partial void OnFollowTerminalChanged(bool value)
    {
        Follow(value ? PickTerminal() : null);
        if (value && _followed is null)
            _status.Set($"Open a terminal to {_host.DisplayName} to follow its folder.", StatusLevel.Error);
        else if (value && _followed?.WorkingDirectory is null)
            _status.Set("The shell does not report its folder yet. The README explains how to turn this on (OSC 7).");
    }

    /// <summary>The most recently used terminal for this host.</summary>
    private TerminalTabViewModel? PickTerminal() =>
        _workspace.LastTerminal is { } last && last.Host.Id == _host.Id
            ? last
            : _workspace.Terminals.LastOrDefault(t => t.Host.Id == _host.Id);

    private void Follow(TerminalTabViewModel? terminal)
    {
        if (_followed == terminal)
            return;
        if (_followed is not null)
            _followed.PropertyChanged -= OnFollowedChanged;
        _followed = terminal;
        if (terminal is null)
            return;
        terminal.PropertyChanged += OnFollowedChanged;
        if (terminal.WorkingDirectory is { } path && path != CurrentPath)
            _ = NavigateToAsync(path);
    }

    private void OnFollowedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTabViewModel.WorkingDirectory) && FollowTerminal && _followed?.WorkingDirectory is { } path && path != CurrentPath)
            _ = NavigateToAsync(path);
    }

    private void OnWorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Switching to another terminal on the same host makes it the one to follow.
        if (FollowTerminal && e.PropertyName == nameof(WorkspaceViewModel.LastTerminal) && PickTerminal() is { } terminal)
            Follow(terminal);
    }

    // ---------------------------------------------------------------- two-panel mode

    /// <summary>The local computer's files next to the server's (created when two-panel mode is first used).</summary>
    [ObservableProperty]
    public partial LocalPaneViewModel? Local { get; private set; }

    [ObservableProperty]
    public partial bool IsDualPane { get; set; }

    [ObservableProperty]
    public partial double LocalPaneWidth { get; set; } = 440;

    [ObservableProperty]
    public partial double TreeWidth { get; set; } = 220;

    [ObservableProperty]
    public partial double PreviewWidth { get; set; } = 340;

    /// <summary>The folder tree gives way to the local panel or the preview, so the file list keeps room.</summary>
    public bool ShowFolderTree => !IsDualPane && !IsPreviewOpen;

    public double LocalPaneMinWidth => 260;

    public double LocalPaneMaxWidth => 1000;

    /// <summary>Text or picture of the selected file, beside the list.</summary>
    public FilePreviewViewModel Preview { get; }

    [ObservableProperty]
    public partial bool IsPreviewOpen { get; set; }

    partial void OnIsPreviewOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFolderTree));
        _settings.Update(s => s with { SftpPreview = value });
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var file = IsPreviewOpen && SelectedEntries.Count == 1 && !SelectedEntries[0].IsDirectory ? SelectedEntries[0].Entry : null;
        _ = Preview.ShowAsync(file);
    }

    partial void OnIsDualPaneChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFolderTree));
        _settings.Update(s => s with { SftpDualPane = value });
        if (value && Local is null)
        {
            Local = new LocalPaneViewModel(_localFiles, _dialogs, _shell, _status);
            var start = _settings.Current.LastLocalDirectory is { } last && Directory.Exists(last) ? last : DefaultDownloads();
            _ = Local.NavigateToAsync(Directory.Exists(start) ? start : _localFiles.HomeDirectory);
        }
    }

    /// <summary>F5 in the server list: copy to the local panel in two-panel mode, otherwise refresh.</summary>
    [RelayCommand]
    private Task RemoteF5() => IsDualPane ? CopyToLocal() : Refresh();

    /// <summary>Downloads the selected server items into the folder shown in the local panel.</summary>
    [RelayCommand]
    private Task CopyToLocal()
    {
        DownloadTo(SelectedEntries.ToList(), Local?.CurrentPath);
        return Task.CompletedTask;
    }

    /// <summary>Uploads the items selected in the local panel into the current server folder.</summary>
    [RelayCommand]
    private void CopyToRemote()
    {
        if (Local is null)
            return;
        UploadPaths(Local.SelectedEntries.Select(e => e.Entry.FullPath).ToList(), CurrentPath);
    }

    /// <summary>Server items dropped on the local panel (or a folder in it) are downloaded there.</summary>
    [RelayCommand]
    private void DropOnLocal(EntryDropRequest? request)
    {
        if (request?.Payload is not RemoteEntryViewModel dragged || Local is null)
            return;
        var folder = request.Target is LocalEntryViewModel { IsDirectory: true } target ? target.Entry.FullPath : Local.CurrentPath;
        var entries = SelectedEntries.Contains(dragged) ? SelectedEntries.ToList() : [dragged];
        DownloadTo(entries, folder);
    }

    private void DownloadTo(IReadOnlyList<RemoteEntryViewModel> entries, string? folder)
    {
        if (_session is null || entries.Count == 0)
            return;
        if (string.IsNullOrEmpty(folder))
        {
            _status.Set("Open a folder in the local panel first.", StatusLevel.Error);
            return;
        }

        foreach (var entry in entries)
            _transfers.EnqueueDownload(_session, _host.DisplayName, entry.Entry, folder);
    }

    public override AppIcon Icon => AppIcon.FolderTree;

    public HostProfile Host => _host;

    public ObservableCollection<RemoteEntryViewModel> Entries { get; } = [];

    public ObservableCollection<RemoteEntryViewModel> SelectedEntries { get; } = [];

    public ObservableCollection<RemoteFolderNodeViewModel> Folders { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting), nameof(IsFailed), nameof(IsReady))]
    public partial SessionState State { get; private set; } = SessionState.Connecting;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    public partial string CurrentPath { get; private set; } = "/";

    [ObservableProperty]
    public partial string PathInput { get; set; } = "/";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsEditingPath { get; set; }

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RemoteFolderNodeViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial bool IsEmptyFolder { get; private set; }

    public bool IsConnecting => State == SessionState.Connecting;

    public bool IsFailed => State is SessionState.Failed or SessionState.Disconnected;

    public bool IsReady => State == SessionState.Connected;

    public bool HasSelection => SelectedEntries.Count > 0;

    public bool HasSingleSelection => SelectedEntries.Count == 1;

    public string ConnectingText => $"Opening SFTP session to {_host.Endpoint}…";

    public override string? StatusDetail => IsReady
        ? $"{Entries.Count} items{(SelectedEntries.Count > 0 ? $"  ·  {SelectedEntries.Count} selected" : string.Empty)}"
        : null;

    /// <summary>Cancelled when the tab closes (stops a pre-connect command that is still running).</summary>
    private readonly CancellationTokenSource _closed = new();

    public async Task ConnectAsync(bool forcePasswordPrompt = false)
    {
        State = SessionState.Connecting;
        ErrorMessage = null;
        try
        {
            var target = await _resolver.ResolveAsync(_host, _password, forcePasswordPrompt, _closed.Token);
            if (target is null)
            {
                Fail("Connection cancelled.");
                return;
            }

            _password = target.Password;
            _session = await _sessions.ConnectAsync(target);
            _session.Disconnected += (_, message) => Dispatcher.UIThread.Post(() =>
            {
                if (State == SessionState.Connected)
                {
                    State = SessionState.Disconnected;
                    ErrorMessage = message ?? "The SFTP session was closed.";
                }
            });

            State = SessionState.Connected;
            _activity.Add(Sshelfy.Core.Activity.ActivityKind.Connected, _host.DisplayName, $"SFTP session to {target.Route} ({target.Endpoint})");
            var root = new RemoteFolderNodeViewModel(this, "/", "/");
            Folders.Add(root);
            await NavigateToAsync(_startPath ?? _session.HomeDirectory, addToHistory: false);
            if (_startPath is not null && CurrentPath != _startPath)
                await NavigateToAsync(_session.HomeDirectory, addToHistory: false);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            // The tab closed while connecting.
        }
        catch (SshAuthenticationFailedException) when (!forcePasswordPrompt && _host.AuthMethod == AuthMethod.Password)
        {
            // Saved password rejected: ask once, like the terminal does.
            await ConnectAsync(forcePasswordPrompt: true);
        }
        catch (SshConnectionFailedException ex)
        {
            _activity.Add(ex is SshAuthenticationFailedException ? Sshelfy.Core.Activity.ActivityKind.LoginFailed : Sshelfy.Core.Activity.ActivityKind.ConnectionFailed,
                _host.DisplayName, ex.Message);
            Fail(ex.Message);
        }
    }

    // ---------------------------------------------------------------- navigation

    [RelayCommand]
    private Task Navigate(string? path) => string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : NavigateToAsync(path.Trim());

    [RelayCommand]
    private Task SubmitPath()
    {
        IsEditingPath = false;
        return NavigateToAsync(PathInput.Trim());
    }

    [RelayCommand]
    private void EditPath()
    {
        PathInput = CurrentPath;
        IsEditingPath = true;
    }

    [RelayCommand]
    private void CancelPathEdit()
    {
        PathInput = CurrentPath;
        IsEditingPath = false;
    }

    private bool CanGoUp => CurrentPath != "/";

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task GoUp() => NavigateToAsync(RemotePath.Parent(CurrentPath));

    [RelayCommand]
    private Task GoBack()
    {
        if (_back.Count == 0)
            return Task.CompletedTask;
        _forward.Push(CurrentPath);
        return NavigateToAsync(_back.Pop(), addToHistory: false);
    }

    [RelayCommand]
    private Task GoForward()
    {
        if (_forward.Count == 0)
            return Task.CompletedTask;
        _back.Push(CurrentPath);
        return NavigateToAsync(_forward.Pop(), addToHistory: false);
    }

    [RelayCommand]
    private Task GoHome() => _session is null ? Task.CompletedTask : NavigateToAsync(_session.HomeDirectory);

    [RelayCommand]
    private Task Refresh() => NavigateToAsync(CurrentPath, addToHistory: false);

    /// <summary>Double-click / Enter: open folders, edit small files, download the rest.</summary>
    [RelayCommand]
    private async Task Open(RemoteEntryViewModel? entry)
    {
        entry ??= SelectedEntries.FirstOrDefault();
        if (entry is null)
            return;

        if (entry.IsDirectory)
            await NavigateToAsync(entry.Entry.FullPath);
        else if (entry.Entry.Size <= MaxEditableSize)
            await EditAsync(entry);
        else
            await Download();
    }

    public async Task NavigateToAsync(string path, bool addToHistory = true)
    {
        if (_session is null)
            return;

        if (_listCts is not null)
            await _listCts.CancelAsync();
        _listCts = new CancellationTokenSource();
        var token = _listCts.Token;

        IsLoading = true;
        try
        {
            var items = await _session.ListAsync(path, token);
            token.ThrowIfCancellationRequested();

            if (addToHistory && path != CurrentPath)
            {
                _back.Push(CurrentPath);
                _forward.Clear();
            }

            CurrentPath = path;
            PathInput = path;
            _allEntries = SortEntries(items.Select(e => new RemoteEntryViewModel(e))).ToList();
            ApplyFilter();

            Breadcrumbs.Clear();
            foreach (var (name, segmentPath) in RemotePath.Segments(path))
                Breadcrumbs.Add(new BreadcrumbItem(name, segmentPath));

            _ = RevealInTreeAsync(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SftpOperationException ex)
        {
            _status.Set($"{path}: {ex.Message}", StatusLevel.Error);
            PathInput = CurrentPath;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    internal async Task<IReadOnlyList<RemoteFileEntry>> ListFoldersAsync(string path)
    {
        if (_session is null)
            return [];
        try
        {
            var entries = await _session.ListAsync(path);
            return entries
                .Where(e => e.IsDirectory && (ShowHidden || !e.Name.StartsWith('.')))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (SftpOperationException)
        {
            return [];
        }
    }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it.</summary>
    private async Task RevealInTreeAsync(string path)
    {
        if (Folders.Count == 0)
            return;

        var node = Folders[0];
        foreach (var (_, segmentPath) in RemotePath.Segments(path).Skip(1))
        {
            await node.EnsureLoadedAsync();
            node.IsExpanded = true;
            var next = node.Children.FirstOrDefault(c => c.Path == segmentPath);
            if (next is null)
                break;
            node = next;
        }

        _revealing = true;
        SelectedFolder = node;
        _revealing = false;
    }

    private bool _revealing;

    partial void OnSelectedFolderChanged(RemoteFolderNodeViewModel? value)
    {
        if (!_revealing && value is not null && value.Path != CurrentPath && value.Path.Length > 0)
            _ = NavigateToAsync(value.Path);
    }

    partial void OnShowHiddenChanged(bool value)
    {
        ApplyFilter();
        foreach (var root in Folders)
            root.Invalidate();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = Filter.Trim();
        Entries.Clear();
        foreach (var entry in _allEntries)
        {
            if (!ShowHidden && entry.IsHidden)
                continue;
            if (filter.Length > 0 && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Entries.Add(entry);
        }

        IsEmptyFolder = Entries.Count == 0;
        OnPropertyChanged(nameof(StatusDetail));
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(StatusDetail));
        UpdatePreview();
    }

    // ---------------------------------------------------------------- operations

    [RelayCommand]
    private async Task NewFolder()
    {
        if (_session is null)
            return;

        var name = await _dialogs.PromptAsync("New folder", "Name", confirmText: "Create");
        if (name is null)
            return;
        if (!RemotePath.IsValidName(name))
        {
            _status.Set("That is not a valid folder name.", StatusLevel.Error);
            return;
        }

        await RunAsync(() => _session.CreateDirectoryAsync(RemotePath.Combine(CurrentPath, name)), $"Created {name}");
        InvalidateTree(CurrentPath);
    }

    [RelayCommand]
    private async Task Rename(RemoteEntryViewModel? entry)
    {
        entry ??= SelectedEntries.FirstOrDefault();
        if (_session is null || entry is null)
            return;

        var name = await _dialogs.PromptAsync("Rename", "New name", entry.Name, "Rename");
        if (name is null || name == entry.Name)
            return;
        if (!RemotePath.IsValidName(name))
        {
            _status.Set("That is not a valid name.", StatusLevel.Error);
            return;
        }

        await RunAsync(() => _session.RenameAsync(entry.Entry.FullPath, RemotePath.Combine(CurrentPath, name)), $"Renamed to {name}");
        if (entry.IsDirectory)
            InvalidateTree(CurrentPath);
    }

    [RelayCommand]
    private async Task Delete()
    {
        var targets = SelectedEntries.ToList();
        if (_session is null || targets.Count == 0)
            return;

        var what = targets.Count == 1 ? $"\"{targets[0].Name}\"" : $"{targets.Count} items";
        var includesFolder = targets.Any(t => t.IsDirectory);
        var message = $"Delete {what} on {_host.DisplayName}?{(includesFolder ? " Folders are deleted with all of their contents." : string.Empty)} This cannot be undone.";
        if (!await _dialogs.ConfirmAsync("Delete", message, "Delete", isDestructive: true))
            return;

        await RunAsync(async () =>
        {
            foreach (var target in targets)
                await _session.DeleteAsync(target.Entry);
        }, $"Deleted {what}");

        if (includesFolder)
            InvalidateTree(CurrentPath);
    }

    [RelayCommand]
    private async Task Download()
    {
        var targets = SelectedEntries.ToList();
        if (_session is null || targets.Count == 0)
            return;

        if (IsDualPane && Local is { IsDriveList: false } local)
        {
            DownloadTo(targets, local.CurrentPath);
            return;
        }

        var folder = await _files.PickFolderAsync("Download to", _settings.Current.LastLocalDirectory ?? DefaultDownloads());
        if (folder is null)
            return;

        _settings.Update(s => s with { LastLocalDirectory = folder });
        foreach (var target in targets)
            _transfers.EnqueueDownload(_session, _host.DisplayName, target.Entry, folder);
    }

    [RelayCommand]
    private async Task Upload()
    {
        var paths = await _files.OpenFilesAsync("Upload files", allowMultiple: true, _settings.Current.LastLocalDirectory);
        UploadPaths(paths, CurrentPath);
    }

    [RelayCommand]
    private async Task UploadFolder()
    {
        var folder = await _files.PickFolderAsync("Upload folder", _settings.Current.LastLocalDirectory);
        if (folder is not null)
            UploadPaths([folder], CurrentPath);
    }

    /// <summary>Files or folders dropped from the OS onto the list (or a folder in it).</summary>
    [RelayCommand]
    private void DropFiles(FileDropRequest? request)
    {
        if (request is null)
            return;
        var targetDirectory = request.Target is RemoteEntryViewModel { IsDirectory: true } folder ? folder.Entry.FullPath
            : request.Target is RemoteFolderNodeViewModel node ? node.Path
            : CurrentPath;
        UploadPaths(request.Paths, targetDirectory);
    }

    private static bool CanMoveEntries(EntryDropRequest? request) =>
        request?.Payload is LocalEntryViewModel
        || (request?.Payload is RemoteEntryViewModel
            && request.Target is RemoteEntryViewModel { IsDirectory: true } or RemoteFolderNodeViewModel { Path.Length: > 0 });

    /// <summary>
    /// Remote entries dragged onto a folder (in the list or the tree) are moved there; local entries
    /// from the two-panel view are uploaded (into the folder under the pointer, else the current one).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveEntries))]
    private async Task MoveEntries(EntryDropRequest? request)
    {
        if (_session is null || request is null)
            return;

        if (request.Payload is LocalEntryViewModel local && Local is not null)
        {
            var uploadTo = request.Target switch
            {
                RemoteEntryViewModel { IsDirectory: true } folder => folder.Entry.FullPath,
                RemoteFolderNodeViewModel node => node.Path,
                _ => CurrentPath,
            };
            var paths = (Local.SelectedEntries.Contains(local) ? Local.SelectedEntries.ToList() : [local]).Select(e => e.Entry.FullPath).ToList();
            UploadPaths(paths, uploadTo);
            return;
        }

        var targetDirectory = request.Target switch
        {
            RemoteEntryViewModel { IsDirectory: true } folder => folder.Entry.FullPath,
            RemoteFolderNodeViewModel node => node.Path,
            _ => null,
        };
        if (targetDirectory is null)
            return;

        var dragged = request.Payload as RemoteEntryViewModel;
        var entries = dragged is not null && SelectedEntries.Contains(dragged) ? SelectedEntries.ToList()
            : dragged is not null ? [dragged] : [];
        entries = entries.Where(e => e.Entry.FullPath != targetDirectory && RemotePath.Parent(e.Entry.FullPath) != targetDirectory).ToList();
        if (entries.Count == 0)
            return;

        await RunAsync(async () =>
        {
            foreach (var entry in entries)
                await _session.RenameAsync(entry.Entry.FullPath, RemotePath.Combine(targetDirectory, entry.Name));
        }, $"Moved {entries.Count} item(s) to {targetDirectory}");
        InvalidateTree(CurrentPath);
        InvalidateTree(targetDirectory);
    }

    /// <summary>Alt+Enter: details with editable permissions and owner.</summary>
    [RelayCommand]
    private async Task Properties(RemoteEntryViewModel? entry)
    {
        entry ??= SelectedEntries.FirstOrDefault();
        if (_session is null || entry is null)
            return;

        try
        {
            var details = await _session.GetDetailsAsync(entry.Entry.FullPath);
            if (await _dialogs.ShowAsync(new Dialogs.RemotePropertiesDialogViewModel(_session, entry.Entry, details)))
            {
                _status.Set($"Updated {entry.Name}", StatusLevel.Ok);
                await NavigateToAsync(CurrentPath, addToHistory: false);
            }
        }
        catch (SftpOperationException ex)
        {
            _status.Set($"{entry.Name}: {ex.Message}", StatusLevel.Error);
        }
    }

    [RelayCommand]
    private async Task CopyPath(RemoteEntryViewModel? entry)
    {
        var path = (entry ?? SelectedEntries.FirstOrDefault())?.Entry.FullPath ?? CurrentPath;
        await _clipboard.SetTextAsync(path);
        _status.Set($"Copied {path}");
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        // Edits are bound to the old session; the file must be opened again to follow the new one.
        await StopExternalEditsAsync();
        Folders.Clear();
        Entries.Clear();
        await ConnectAsync();
    }

    private void UploadPaths(IReadOnlyList<string> paths, string remoteDirectory)
    {
        if (_session is null || paths.Count == 0)
            return;

        _settings.Update(s => s with { LastLocalDirectory = Path.GetDirectoryName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) });
        foreach (var path in paths)
            _transfers.EnqueueUpload(_session, _host.DisplayName, path, remoteDirectory);
    }

    private async Task EditAsync(RemoteEntryViewModel entry)
    {
        if (_session is null)
            return;

        var existing = _workspace.Find<EditorTabViewModel>(e => e.Session == _session && e.RemotePath == entry.Entry.FullPath);
        if (existing is not null)
        {
            _workspace.ActiveTab = existing;
            return;
        }

        try
        {
            using var buffer = new MemoryStream();
            await _session.DownloadAsync(entry.Entry.FullPath, buffer, null);
            var bytes = buffer.ToArray();
            if (EditorTabViewModel.LooksBinary(bytes))
            {
                if (await _dialogs.ConfirmAsync("Binary file", $"\"{entry.Name}\" does not look like text. Download it instead?", "Download"))
                    await Download();
                return;
            }

            var editor = new EditorTabViewModel(this, _session, entry.Entry.FullPath, bytes, _dialogs, _status);
            _workspace.Open(editor);
        }
        catch (SftpOperationException ex)
        {
            _status.Set($"{entry.Name}: {ex.Message}", StatusLevel.Error);
        }
    }

    // ---------------------------------------------------------------- folder compare

    /// <summary>Compares the local panel's folder with the server folder (two-panel mode) and queues the chosen copies.</summary>
    [RelayCommand]
    private async Task CompareFolders()
    {
        if (_session is null || Local?.CurrentPath is not { Length: > 0 } localRoot)
        {
            _status.Set("Open a folder in the local panel first.", StatusLevel.Error);
            return;
        }

        var dialog = new Dialogs.CompareFoldersDialogViewModel(_localFiles, localRoot, _session, CurrentPath, _transfers, _host.DisplayName);
        _ = dialog.LoadAsync();
        var queued = await _dialogs.ShowAsync(dialog);
        if (queued > 0)
            _status.Set($"Copying {queued} item{(queued == 1 ? string.Empty : "s")} between {Path.GetFileName(localRoot.TrimEnd(Path.DirectorySeparatorChar))} and {CurrentPath}", StatusLevel.Ok);
    }

    // ---------------------------------------------------------------- external editor

    private readonly List<ExternalEditSync> _externalEdits = [];

    private static string ExternalEditRoot => Path.Combine(Path.GetTempPath(), "sshelfy", "edit");

    /// <summary>Opens the file in the editor from Settings (or the system default); each save is uploaded back while this tab is open.</summary>
    [RelayCommand]
    private async Task EditExternally(RemoteEntryViewModel? entry)
    {
        entry ??= SelectedEntries.FirstOrDefault();
        if (_session is null || entry is null || entry.IsDirectory)
            return;

        try
        {
            var sync = _externalEdits.FirstOrDefault(e => e.RemotePath == entry.Entry.FullPath && e.Session == _session);
            if (sync is null)
            {
                sync = await ExternalEditSync.StartAsync(_session, entry.Entry, ExternalEditRoot, new ExternalEditCallbacks(this));
                _externalEdits.Add(sync);
            }

            if (_shell.OpenInEditor(sync.LocalPath, _settings.Current.ExternalEditor) is { } error)
            {
                _status.Set(error, StatusLevel.Error);
                return;
            }

            _status.Set($"Editing {entry.Name}: saved changes upload to {_host.DisplayName} while this tab is open", StatusLevel.Ok);
        }
        catch (Exception ex) when (ex is SftpOperationException or IOException or UnauthorizedAccessException)
        {
            _status.Set($"{entry.Name}: {ex.Message}", StatusLevel.Error);
        }
    }

    /// <summary>Uploads any last save, then stops watching; one failing edit must not keep the tab from closing.</summary>
    private async Task StopExternalEditsAsync()
    {
        foreach (var edit in _externalEdits)
        {
            try
            {
                await edit.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SftpOperationException or ObjectDisposedException)
            {
                _status.Set($"Could not upload {RemotePath.Name(edit.RemotePath)}: {ex.Message}", StatusLevel.Error);
            }
        }

        _externalEdits.Clear();
    }

    /// <summary>Reports uploads on the UI thread and asks before overwriting a file someone else changed.</summary>
    private sealed class ExternalEditCallbacks(SftpTabViewModel tab) : IExternalEditCallbacks
    {
        public Task<bool> ConfirmOverwriteAsync(string remotePath) => Dispatcher.UIThread.InvokeAsync(() =>
            tab._dialogs.ConfirmAsync("File changed on the server",
                $"{RemotePath.Name(remotePath)} changed on {tab._host.DisplayName} since you opened it. Upload your version over it?",
                "Overwrite", isDestructive: true));

        public void Uploaded(string remotePath) => Dispatcher.UIThread.Post(() =>
        {
            tab._status.Set($"Uploaded {RemotePath.Name(remotePath)} to {tab._host.DisplayName}", StatusLevel.Ok);
            _ = tab.RefreshIfShowingAsync(RemotePath.Parent(remotePath));
        });

        public void Failed(string remotePath, string message) =>
            Dispatcher.UIThread.Post(() => tab._status.Set($"Could not upload {RemotePath.Name(remotePath)}: {message}", StatusLevel.Error));
    }

    /// <summary>Keeps the session alive while an editor tab still uses it.</summary>
    internal void AcquireSession() => _leases++;

    internal void ReleaseSession()
    {
        _leases--;
        if (_leases == 0 && _disposeRequested && _session is { } session)
        {
            _session = null;
            _ = session.DisposeAsync();
        }
    }

    internal Task RefreshIfShowingAsync(string directory) =>
        directory == CurrentPath ? NavigateToAsync(CurrentPath, addToHistory: false) : Task.CompletedTask;

    private void InvalidateTree(string path)
    {
        RemoteFolderNodeViewModel? Find(IEnumerable<RemoteFolderNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Path == path)
                    return node;
                if (path.StartsWith(node.Path, StringComparison.Ordinal) && Find(node.Children) is { } found)
                    return found;
            }

            return null;
        }

        Find(Folders)?.Invalidate();
    }

    private async Task RunAsync(Func<Task> operation, string successMessage)
    {
        try
        {
            IsLoading = true;
            await operation();
            _status.Set(successMessage, StatusLevel.Ok);
        }
        catch (SftpOperationException ex)
        {
            _status.Set(ex.Message, StatusLevel.Error);
        }
        finally
        {
            IsLoading = false;
            await NavigateToAsync(CurrentPath, addToHistory: false);
        }
    }

    private void Fail(string message)
    {
        State = SessionState.Failed;
        ErrorMessage = message;
        _status.Set(message, StatusLevel.Error);
    }

    private static string DefaultDownloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private void OnTransferFinished(object? sender, TransferItem item)
    {
        // Show freshly uploaded files without a manual refresh.
        if (item.Direction == TransferDirection.Upload && item.State == TransferState.Completed && _session is not null)
            _ = RefreshIfShowingAsync(RemotePath.Parent(item.RemotePath));
        else if (item.Direction == TransferDirection.Download && item.State == TransferState.Completed && Local is not null)
            _ = Local.RefreshIfShowingAsync(Path.GetDirectoryName(item.LocalPath));
    }

    public override async ValueTask DisposeAsync()
    {
        // Upload any last save before the session goes away.
        await _closed.CancelAsync();
        await StopExternalEditsAsync();
        Preview.Dispose();

        _transfers.Finished -= OnTransferFinished;
        _workspace.PropertyChanged -= OnWorkspaceChanged;
        Follow(null);
        _listCts?.Cancel();
        if (_session is { } session)
        {
            _transfers.CancelForSession(session);
            _disposeRequested = true;
            if (_leases == 0)
            {
                _session = null;
                await session.DisposeAsync();
            }
        }
    }
}

/// <summary>Local paths dropped onto the browser; <see cref="Target"/> is the item under the pointer.</summary>
public sealed record FileDropRequest(IReadOnlyList<string> Paths, object? Target);

/// <summary>An in-app drag of <see cref="Payload"/> dropped onto <see cref="Target"/>.</summary>
public sealed record EntryDropRequest(object Payload, object? Target);
