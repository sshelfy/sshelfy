using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Hosts;

namespace Sshelfy.App.ViewModels.Sidebar;

/// <summary>Host tree with folders, search and quick actions.</summary>
public sealed partial class HostsSidebarViewModel : SidebarSectionViewModel
{
    private readonly IHostCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly IVaultGate _vaultGate;
    private readonly IFilePickerService _files;
    private readonly ISessionLauncher _launcher;
    private readonly StatusBarViewModel _status;
    private readonly Sshelfy.Core.Import.SshConfigImporter _importer;
    private readonly Sshelfy.Core.Import.ILocalSshFiles _sshFiles;
    private readonly Sshelfy.Core.Ssh.ISshAgentClient _agent;

    public HostsSidebarViewModel(
        IHostCatalog catalog,
        IDialogService dialogs,
        IVaultGate vaultGate,
        IFilePickerService files,
        ISessionLauncher launcher,
        StatusBarViewModel status,
        Sshelfy.Core.Import.SshConfigImporter importer,
        Sshelfy.Core.Import.ILocalSshFiles sshFiles,
        Sshelfy.Core.Ssh.ISshAgentClient agent,
        IKeyService keys,
        PreConnectGate? preConnect = null)
    {
        _keys = keys;
        _preConnect = preConnect;
        _agent = agent;
        _importer = importer;
        _sshFiles = sshFiles;
        _catalog = catalog;
        _dialogs = dialogs;
        _vaultGate = vaultGate;
        _files = files;
        _launcher = launcher;
        _status = status;

        _catalog.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    public override string Title => "Hosts";

    public override AppIcon Icon => AppIcon.Server;

    public ObservableCollection<HostTreeNodeViewModel> Nodes { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedCommand))]
    public partial HostTreeNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    public partial bool IsCatalogEmpty { get; private set; }

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasNoResults => !IsCatalogEmpty && IsSearching && Nodes.Count == 0;

    /// <summary>Search text that looks like user@host can be connected to directly.</summary>
    public bool CanQuickConnectFromSearch => QuickConnectParser.TryParse(SearchText, out _, out _, out _);

    partial void OnSearchTextChanged(string value)
    {
        Rebuild();
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(CanQuickConnectFromSearch));
    }

    internal void OnGroupExpansionChanged(GroupNodeViewModel node, bool isExpanded)
    {
        if (!IsSearching)
            _ = _catalog.SetGroupExpandedAsync(node.Id, isExpanded);
    }

    [RelayCommand]
    private async Task NewHost(HostTreeNodeViewModel? context)
    {
        var groupId = (context ?? SelectedNode) switch
        {
            GroupNodeViewModel g => g.Id,
            HostNodeViewModel h => h.Host.GroupId,
            _ => null,
        };

        var saved = await _dialogs.ShowAsync(new HostEditorDialogViewModel(_catalog, _dialogs, _vaultGate, _files, _agent, null, groupId, false, _keys));
        if (saved is not null)
        {
            _preConnect?.Approve(saved);
            Select(saved.Id);
            _status.Set($"Saved {saved.DisplayName}");
        }
    }

    [RelayCommand]
    private async Task NewGroup(HostTreeNodeViewModel? context)
    {
        Guid? parentId = context is GroupNodeViewModel g ? g.Id : null;
        var name = await _dialogs.PromptAsync(parentId is null ? "New group" : "New subgroup", "Name", confirmText: "Create");
        if (name is null)
            return;

        var group = new HostGroup { Name = name, ParentId = parentId };
        await _catalog.SaveGroupAsync(group);
        Select(group.Id);
    }

    [RelayCommand]
    private async Task Edit(HostTreeNodeViewModel? node)
    {
        switch (node ?? SelectedNode)
        {
            case HostNodeViewModel h:
                var hasPassword = await _catalog.HasSavedPasswordAsync(h.Host.Id);
                var saved = await _dialogs.ShowAsync(
                    new HostEditorDialogViewModel(_catalog, _dialogs, _vaultGate, _files, _agent, h.Host, h.Host.GroupId, hasPassword, _keys));
                if (saved is not null)
                {
                    // A command the user changed is theirs; an unchanged one (maybe imported) still asks before its first run.
                    if (saved.PreConnectCommand != h.Host.PreConnectCommand)
                        _preConnect?.Approve(saved);
                    Select(saved.Id);
                }
                break;

            case GroupNodeViewModel g:
                var name = await _dialogs.PromptAsync("Rename group", "Name", g.Name, "Rename");
                if (name is not null && name != g.Name)
                    await _catalog.SaveGroupAsync(g.Group with { Name = name });
                break;
        }
    }

    [RelayCommand]
    private async Task Duplicate(HostTreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is not HostNodeViewModel h)
            return;

        // Secrets are not copied: the copy asks for its own password.
        var copy = h.Host with
        {
            Id = Guid.NewGuid(),
            Name = $"{h.Host.DisplayName} copy",
            CreatedAt = DateTimeOffset.UtcNow,
            LastConnectedAt = null,
        };
        await _catalog.SaveHostAsync(copy);
        Select(copy.Id);
    }

    [RelayCommand]
    private async Task Delete(HostTreeNodeViewModel? node)
    {
        switch (node ?? SelectedNode)
        {
            case HostNodeViewModel h:
                if (await _dialogs.ConfirmAsync("Delete host", $"Delete \"{h.Name}\" and its saved password? This cannot be undone.", "Delete", true))
                {
                    await _catalog.DeleteHostAsync(h.Id);
                    _status.Set($"Deleted {h.Name}");
                }

                break;

            case GroupNodeViewModel g:
                var message = g.HostCount == 0
                    ? $"Delete the group \"{g.Name}\"?"
                    : $"Delete the group \"{g.Name}\"? Its {g.HostCount} host(s) will move to the parent group.";
                if (await _dialogs.ConfirmAsync("Delete group", message, "Delete", true))
                    await _catalog.DeleteGroupAsync(g.Id);
                break;
        }
    }

    [RelayCommand]
    private Task Connect(HostTreeNodeViewModel? node) =>
        (node ?? SelectedNode) is HostNodeViewModel h ? _launcher.ConnectAsync(h.Host) : Task.CompletedTask;

    private readonly IKeyService _keys;
    private readonly PreConnectGate? _preConnect;

    /// <summary>Puts a key's public half on the host so it can log in without a password.</summary>
    [RelayCommand]
    private Task InstallKey(HostTreeNodeViewModel? node) =>
        (node ?? SelectedNode) is HostNodeViewModel h ? _keys.InstallOnHostAsync(h.Host) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenSftp(HostTreeNodeViewModel? node) =>
        (node ?? SelectedNode) is HostNodeViewModel h ? _launcher.OpenSftpAsync(h.Host) : Task.CompletedTask;

    private bool CanConnectSelected => SelectedNode is HostNodeViewModel;

    [RelayCommand(CanExecute = nameof(CanConnectSelected))]
    private Task ConnectSelected() => Connect(SelectedNode);

    /// <summary>Enter in the search box: quick connect if it looks like user@host, else connect to the first match.</summary>
    [RelayCommand]
    private async Task SubmitSearch()
    {
        if (CanQuickConnectFromSearch)
        {
            await _launcher.QuickConnectAsync(SearchText);
            return;
        }

        if (FirstHost(Nodes) is { } first)
            await _launcher.ConnectAsync(first.Host);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private async Task ImportSshConfig()
    {
        var result = await _dialogs.ShowAsync(new SshImportDialogViewModel(_importer, _catalog, _dialogs, _vaultGate, _sshFiles.ConfigPath));
        if (result is null)
            return;

        var parts = new List<string> { result.HostsImported == 1 ? "1 host" : $"{result.HostsImported} hosts" };
        if (result.KeysImported > 0)
            parts.Add(result.KeysImported == 1 ? "1 key" : $"{result.KeysImported} keys");
        if (result.KnownHostsAdded > 0)
            parts.Add($"{result.KnownHostsAdded} host keys");
        _status.Set($"Imported {string.Join(", ", parts)}", StatusLevel.Ok);

        if (result.Warnings.Count > 0)
            await _dialogs.ConfirmAsync("Imported with notes", string.Join("\n\n", result.Warnings), "OK");
    }

    private void Select(Guid id)
    {
        SelectedNode = Find(Nodes, id);
    }

    private void Rebuild()
    {
        var selectedId = SelectedNode?.Id;
        var filter = SearchText.Trim();
        var searching = filter.Length > 0;

        Nodes.Clear();
        foreach (var node in BuildLevel(null, filter, searching))
            Nodes.Add(node);

        IsCatalogEmpty = _catalog.Hosts.Count == 0 && _catalog.Groups.Count == 0;
        OnPropertyChanged(nameof(HasNoResults));

        if (selectedId is { } id)
            SelectedNode = Find(Nodes, id);
    }

    private IEnumerable<HostTreeNodeViewModel> BuildLevel(Guid? parentId, string filter, bool searching)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;

        foreach (var group in _catalog.Groups.Where(g => g.ParentId == parentId).OrderBy(g => g.Name, comparer))
        {
            var node = new GroupNodeViewModel(this, group, searching || group.IsExpanded);
            foreach (var child in BuildLevel(group.Id, filter, searching))
                node.Children.Add(child);

            var groupMatches = searching && group.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
            if (!searching || groupMatches || node.Children.Count > 0)
            {
                if (groupMatches && node.Children.Count == 0)
                {
                    // Show the full content of a group whose name matches.
                    foreach (var child in BuildLevel(group.Id, string.Empty, false))
                        node.Children.Add(child);
                }

                yield return node;
            }
        }

        foreach (var host in _catalog.Hosts.Where(h => h.GroupId == parentId).OrderBy(h => h.DisplayName, comparer))
        {
            if (searching && !Matches(host, filter))
                continue;

            var keyName = host.KeyId is { } keyId ? _catalog.FindKey(keyId)?.Name : null;
            var jumpName = host.JumpHostId is { } jumpId ? _catalog.FindHost(jumpId)?.DisplayName : null;
            yield return new HostNodeViewModel(this, host, keyName, jumpName);
        }
    }

    private static bool Matches(HostProfile host, string filter) =>
        host.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || host.Address.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || host.Username.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || (host.Notes?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static HostTreeNodeViewModel? Find(IEnumerable<HostTreeNodeViewModel> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
                return node;
            if (Find(node.Children, id) is { } found)
                return found;
        }

        return null;
    }

    private static HostNodeViewModel? FirstHost(IEnumerable<HostTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is HostNodeViewModel h)
                return h;
            if (FirstHost(node.Children) is { } nested)
                return nested;
        }

        return null;
    }
}
