using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Sftp;

/// <summary>A row in the remote file list.</summary>
public sealed class RemoteEntryViewModel : ViewModelBase
{
    public RemoteEntryViewModel(RemoteFileEntry entry)
    {
        Entry = entry;
    }

    public RemoteFileEntry Entry { get; }

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsHidden => Entry.Name.StartsWith('.');

    public AppIcon Icon => Entry.IsDirectory ? AppIcon.Folder : Entry.IsSymlink ? AppIcon.Link : AppIcon.File;

    public string SizeText => Entry.IsDirectory ? string.Empty : ByteSize.Format(Entry.Size);

    public string ModifiedText => Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string Permissions => Entry.Permissions;
}

/// <summary>A directory in the folder tree; children load when it is first expanded.</summary>
public sealed partial class RemoteFolderNodeViewModel : ViewModelBase
{
    private static readonly RemoteFolderNodeViewModel Placeholder = new(null!, "…", "", isPlaceholder: true);

    private readonly SftpTabViewModel _owner;
    private bool _loaded;

    public RemoteFolderNodeViewModel(SftpTabViewModel owner, string name, string path, bool isPlaceholder = false)
    {
        _owner = owner;
        Name = name;
        Path = path;
        if (!isPlaceholder)
            Children.Add(Placeholder);
    }

    public string Name { get; }

    public string Path { get; }

    public ObservableCollection<RemoteFolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = EnsureLoadedAsync();
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;
        _loaded = true;

        var folders = await _owner.ListFoldersAsync(Path);
        Children.Clear();
        foreach (var folder in folders)
            Children.Add(new RemoteFolderNodeViewModel(_owner, folder.Name, folder.FullPath));
    }

    public void Invalidate()
    {
        _loaded = false;
        if (IsExpanded)
            _ = EnsureLoadedAsync();
    }
}

public sealed record BreadcrumbItem(string Name, string Path);
