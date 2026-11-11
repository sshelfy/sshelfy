using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>A difference between the two folders and what to do about it.</summary>
public sealed partial class CompareItemViewModel(CompareItem item) : ObservableObject
{
    public CompareItem Item { get; } = item;

    public string Path => Item.RelativePath + (IsDirectory ? "/" : string.Empty);

    public bool IsDirectory => Item.Local?.IsDirectory ?? Item.Remote?.IsDirectory ?? false;

    public string StatusText => Item.Status switch
    {
        CompareStatus.Same => "Same",
        CompareStatus.OnlyLocal => "Only on this PC",
        CompareStatus.OnlyRemote => "Only on the server",
        CompareStatus.LocalNewer => "Newer on this PC",
        CompareStatus.RemoteNewer => "Newer on the server",
        CompareStatus.Different => "Different size",
        _ => "File here, folder there",
    };

    public string LocalInfo => Item.Local is { } l ? Describe(l.IsDirectory, l.Size, l.Modified) : "—";

    public string RemoteInfo => Item.Remote is { } r ? Describe(r.IsDirectory, r.Size, r.Modified) : "—";

    public bool CanUpload => Item.Local is not null && Item.Status != CompareStatus.TypeMismatch;

    public bool CanDownload => Item.Remote is not null && Item.Status != CompareStatus.TypeMismatch;

    /// <summary>Copy this PC's version to the server.</summary>
    [ObservableProperty]
    public partial bool Upload { get; set; } = item.SuggestedDirection == SyncDirection.Upload;

    /// <summary>Copy the server's version to this PC.</summary>
    [ObservableProperty]
    public partial bool Download { get; set; } = item.SuggestedDirection == SyncDirection.Download;

    public SyncDirection? Direction => Upload ? SyncDirection.Upload : Download ? SyncDirection.Download : null;

    partial void OnUploadChanged(bool value)
    {
        if (value)
            Download = false;
    }

    partial void OnDownloadChanged(bool value)
    {
        if (value)
            Upload = false;
    }

    private static string Describe(bool isDirectory, long size, DateTimeOffset modified) =>
        isDirectory ? "folder" : $"{FormatSize(size)}  ·  {modified.ToLocalTime():yyyy-MM-dd HH:mm}";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

/// <summary>
/// Compares the local panel's folder with the server folder and copies the chosen files through the
/// transfer queue (newer or missing files by default; nothing is ever deleted).
/// </summary>
public sealed partial class CompareFoldersDialogViewModel : DialogViewModel<int>
{
    private readonly ILocalFileSystem _localFiles;
    private readonly ISftpSession _session;
    private readonly TransferQueue _transfers;
    private readonly string _hostName;
    private readonly CancellationTokenSource _cts = new();
    private List<CompareItemViewModel> _all = [];
    private readonly List<string> _skipped = [];

    public CompareFoldersDialogViewModel(ILocalFileSystem localFiles, string localRoot, ISftpSession session, string remoteRoot, TransferQueue transfers, string hostName)
    {
        _localFiles = localFiles;
        _session = session;
        _transfers = transfers;
        _hostName = hostName;
        LocalRoot = localRoot;
        RemoteRoot = remoteRoot;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
                SyncCommand.NotifyCanExecuteChanged();
        };

        // Closing the dialog stops a comparison still listing folders.
        _ = Completion.ContinueWith(_ => _cts.Cancel(), TaskScheduler.Default);
    }

    public override string Title => "Compare folders";

    public override double Width => 760;

    public string LocalRoot { get; }

    public string RemoteRoot { get; }

    public ObservableCollection<CompareItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string? Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool ShowIdentical { get; set; }

    public bool HasItems => Items.Count > 0;

    /// <summary>Listing both folders (Cancel stays available, unlike while copying).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool IsComparing { get; private set; }

    public bool IsEmpty => !IsComparing && Error is null && Items.Count == 0;

    public string Summary
    {
        get
        {
            var different = _all.Count(i => i.Item.Status != CompareStatus.Same);
            var same = _all.Count - different;
            var text = different == 0 ? $"The folders match ({same} files)." : $"{different} difference{(different == 1 ? string.Empty : "s")}, {same} identical.";
            return _skipped.Count == 0 ? text : $"{text} {_skipped.Count} unreadable folder{(_skipped.Count == 1 ? " was" : "s were")} skipped.";
        }
    }

    public int SelectedCount => _all.Count(i => i.Direction is not null);

    public string SyncText => SelectedCount == 0 ? "Copy" : $"Copy {SelectedCount}";

    partial void OnShowIdenticalChanged(bool value) => ApplyFilter();

    /// <summary>Lists both folders and fills <see cref="Items"/>; started by whoever shows the dialog.</summary>
    public async Task LoadAsync()
    {
        IsComparing = true;
        try
        {
            var progress = new Progress<string>(folder => Progress = folder);
            var items = await FolderComparison.CompareAsync(_localFiles, LocalRoot, _session, RemoteRoot, progress, _cts.Token, _skipped);
            _all = items.Select(i => new CompareItemViewModel(i)).ToList();
            foreach (var item in _all)
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(CompareItemViewModel.Upload) or nameof(CompareItemViewModel.Download))
                        RefreshCounts();
                };
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SftpOperationException or LocalFileException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsComparing = false;
            Progress = null;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(IsEmpty));
            RefreshCounts();
        }
    }

    [RelayCommand]
    private void UploadNewer() => Choose(i => i.Item.Status is CompareStatus.OnlyLocal or CompareStatus.LocalNewer, upload: true);

    [RelayCommand]
    private void DownloadNewer() => Choose(i => i.Item.Status is CompareStatus.OnlyRemote or CompareStatus.RemoteNewer, upload: false);

    [RelayCommand]
    private void ClearChoices()
    {
        foreach (var item in _all)
            item.Upload = item.Download = false;
    }

    private void Choose(Func<CompareItemViewModel, bool> filter, bool upload)
    {
        foreach (var item in _all.Where(filter))
        {
            if (upload)
                item.Upload = true;
            else
                item.Download = true;
        }
    }

    private bool CanSync => !IsBusy && !IsComparing && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task Sync()
    {
        IsBusy = true;
        try
        {
            var choices = _all.Where(i => i.Direction is not null).Select(i => (i.Item, i.Direction!.Value)).ToList();
            var queued = await FolderSync.EnqueueAsync(choices, _transfers, _session, _hostName, LocalRoot, RemoteRoot, _cts.Token);
            Close(queued.Count);
        }
        catch (Exception ex) when (ex is SftpOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SyncText));
        SyncCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _all.Where(i => ShowIdentical || i.Item.Status != CompareStatus.Same))
            Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
