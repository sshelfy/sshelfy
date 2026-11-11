using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Sftp;

/// <summary>A row in the local file list of the two-panel browser.</summary>
public sealed class LocalEntryViewModel(LocalFileEntry entry) : ViewModelBase
{
    public LocalFileEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsHidden => Entry.IsHidden;

    public AppIcon Icon => Entry.IsDirectory ? AppIcon.Folder : AppIcon.File;

    public string SizeText => Entry.IsDirectory ? string.Empty : ByteSize.Format(Entry.Size);

    public string ModifiedText => Entry.Modified == DateTimeOffset.MinValue
        ? string.Empty
        : Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>The local side of the two-panel SFTP browser.</summary>
public sealed partial class LocalPaneViewModel : ViewModelBase
{
    private readonly ILocalFileSystem _files;
    private readonly IDialogService _dialogs;
    private readonly IShellIntegration _shell;
    private readonly StatusBarViewModel _status;
    private List<LocalEntryViewModel> _all = [];
    private CancellationTokenSource? _listCts;

    public LocalPaneViewModel(ILocalFileSystem files, IDialogService dialogs, IShellIntegration shell, StatusBarViewModel status)
    {
        _files = files;
        _dialogs = dialogs;
        _shell = shell;
        _status = status;
        SelectedEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSelection));
        Sort.Changed += (_, _) =>
        {
            _all = SortEntries(_all).ToList();
            ApplyFilter();
        };
    }

    public FileSort Sort { get; } = new();

    private IEnumerable<LocalEntryViewModel> SortEntries(IEnumerable<LocalEntryViewModel> entries) =>
        Sort.Apply(entries, e => e.IsDirectory, e => e.Name, e => e.Entry.Size, e => e.Entry.Modified);

    public ObservableCollection<LocalEntryViewModel> Entries { get; } = [];

    public ObservableCollection<LocalEntryViewModel> SelectedEntries { get; } = [];

    /// <summary>The folder shown; empty while the drive list is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPath), nameof(IsDriveList))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    public partial string CurrentPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PathInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public bool IsDriveList => CurrentPath.Length == 0;

    public string DisplayPath => IsDriveList ? "This PC" : CurrentPath;

    public bool HasSelection => SelectedEntries.Count > 0;

    public async Task NavigateToAsync(string path)
    {
        if (_listCts is not null)
            await _listCts.CancelAsync();
        _listCts = new CancellationTokenSource();
        var token = _listCts.Token;

        try
        {
            var items = await _files.ListAsync(path, token);
            token.ThrowIfCancellationRequested();
            CurrentPath = path;
            PathInput = path;
            _all = SortEntries(items.Select(e => new LocalEntryViewModel(e))).ToList();
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (LocalFileException ex)
        {
            _status.Set($"{path}: {ex.Message}", StatusLevel.Error);
            PathInput = CurrentPath;
        }
    }

    public Task RefreshIfShowingAsync(string? directory) =>
        directory is not null && string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(CurrentPath), StringComparison.OrdinalIgnoreCase)
            ? NavigateToAsync(CurrentPath)
            : Task.CompletedTask;

    [RelayCommand]
    private Task SubmitPath() => NavigateToAsync(PathInput.Trim());

    private bool CanGoUp => _files.Parent(CurrentPath) is not null;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task GoUp() => _files.Parent(CurrentPath) is { } parent ? NavigateToAsync(parent) : Task.CompletedTask;

    [RelayCommand]
    private Task GoHome() => NavigateToAsync(_files.HomeDirectory);

    [RelayCommand]
    private Task Refresh() => NavigateToAsync(CurrentPath);

    [RelayCommand]
    private Task Open(LocalEntryViewModel? entry)
    {
        entry ??= SelectedEntries.FirstOrDefault();
        if (entry is null)
            return Task.CompletedTask;
        if (entry.IsDirectory)
            return NavigateToAsync(entry.Entry.FullPath);
        _shell.OpenFile(entry.Entry.FullPath);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Reveal(LocalEntryViewModel? entry)
    {
        var path = (entry ?? SelectedEntries.FirstOrDefault())?.Entry.FullPath;
        if (path is not null)
            _shell.Reveal(path);
        else if (!IsDriveList)
            _shell.OpenFolder(CurrentPath);
    }

    [RelayCommand]
    private async Task NewFolder()
    {
        if (IsDriveList)
            return;
        var name = await _dialogs.PromptAsync("New folder", "Name", confirmText: "Create");
        if (name is null)
            return;
        await RunAsync(() => _files.CreateDirectoryAsync(_files.Combine(CurrentPath, name)), $"Created {name}");
    }

    [RelayCommand]
    private async Task Rename(LocalEntryViewModel? entry)
    {
        entry ??= SelectedEntries.FirstOrDefault();
        if (entry is null || IsDriveList)
            return;
        var name = await _dialogs.PromptAsync("Rename", "New name", entry.Name, "Rename");
        if (name is null || name == entry.Name)
            return;
        await RunAsync(() => _files.RenameAsync(entry.Entry.FullPath, _files.Combine(CurrentPath, name)), $"Renamed to {name}");
    }

    [RelayCommand]
    private async Task Delete()
    {
        var targets = SelectedEntries.ToList();
        if (targets.Count == 0 || IsDriveList)
            return;
        var what = targets.Count == 1 ? $"\"{targets[0].Name}\"" : $"{targets.Count} items";
        if (!await _dialogs.ConfirmAsync("Delete", $"Delete {what} from this computer? This cannot be undone.", "Delete", isDestructive: true))
            return;
        await RunAsync(async () =>
        {
            foreach (var target in targets)
                await _files.DeleteAsync(target.Entry);
        }, $"Deleted {what}");
    }

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var entry in _all)
        {
            if (ShowHidden || !entry.IsHidden)
                Entries.Add(entry);
        }

        IsEmpty = Entries.Count == 0;
    }

    private async Task RunAsync(Func<Task> operation, string success)
    {
        try
        {
            await operation();
            _status.Set(success, StatusLevel.Ok);
        }
        catch (LocalFileException ex)
        {
            _status.Set(ex.Message, StatusLevel.Error);
        }

        await NavigateToAsync(CurrentPath);
    }
}
