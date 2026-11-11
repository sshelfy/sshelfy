using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.History;

namespace Sshelfy.App.ViewModels.Sidebar;

public sealed class HistoryItemViewModel(string hostKey, string hostName, CommandHistoryEntry entry, HistorySidebarViewModel owner) : ViewModelBase
{
    public string HostKey { get; } = hostKey;

    public string HostName { get; } = hostName;

    public CommandHistoryEntry Entry { get; } = entry;

    public HistorySidebarViewModel Owner { get; } = owner;

    public string Command => Entry.Command;

    public string Meta => Entry.UseCount > 1
        ? $"{RelativeTime.Format(Entry.LastUsedAt)}  ·  {Entry.UseCount}×"
        : RelativeTime.Format(Entry.LastUsedAt);
}

/// <summary>Commands run in the current session's host, or recent commands from all hosts.</summary>
public sealed partial class HistorySidebarViewModel : SidebarSectionViewModel
{
    private const int GlobalLimit = 200;

    private readonly ICommandHistory _history;
    private readonly WorkspaceViewModel _workspace;
    private readonly ITerminalCommandSender _sender;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly SnippetsSidebarViewModel _snippets;
    private readonly StatusBarViewModel _status;

    public HistorySidebarViewModel(
        ICommandHistory history,
        WorkspaceViewModel workspace,
        ITerminalCommandSender sender,
        IClipboardService clipboard,
        IDialogService dialogs,
        SnippetsSidebarViewModel snippets,
        StatusBarViewModel status)
    {
        _history = history;
        _workspace = workspace;
        _sender = sender;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _snippets = snippets;
        _status = status;

        _history.Changed += (_, _) => UiThread.Run(Rebuild);
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.LastTerminal))
                Rebuild();
        };
        Rebuild();
    }

    public override string Title => "History";

    public override AppIcon Icon => AppIcon.History;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial HistoryItemViewModel? SelectedItem { get; set; }

    /// <summary>True when showing one host's history (a terminal is open).</summary>
    public bool IsHostScoped => _workspace.LastTerminal is not null;

    public bool IsGlobal => !IsHostScoped;

    public string ScopeText => _workspace.LastTerminal is { } terminal ? terminal.Host.DisplayName : "All hosts";

    public bool IsEmpty => Items.Count == 0 && SearchText.Length == 0 && !IsLocked;

    /// <summary>Saved history is encrypted in the vault, which is locked.</summary>
    public bool IsLocked => Items.Count == 0 && _history.IsLocked;

    public bool HasNoResults => Items.Count == 0 && SearchText.Length > 0;

    partial void OnSearchTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void Run(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null)
            _sender.Send(item.Command, run: true);
    }

    [RelayCommand]
    private void Insert(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null)
            _sender.Send(item.Command, run: false);
    }

    [RelayCommand]
    private async Task Copy(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;
        await _clipboard.SetTextAsync(item.Command);
        _status.Set("Copied command");
    }

    [RelayCommand]
    private Task SaveAsSnippet(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        return item is null ? Task.CompletedTask : _snippets.CreateFromCommandAsync(item.Command);
    }

    [RelayCommand]
    private void Remove(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null)
            _history.Remove(item.HostKey, item.Command);
    }

    [RelayCommand]
    private async Task ClearHost()
    {
        if (_workspace.LastTerminal is not { } terminal)
            return;
        if (await _dialogs.ConfirmAsync("Clear history", $"Forget all remembered commands for {terminal.Host.DisplayName}?", "Clear", isDestructive: true))
            _history.Clear(terminal.HistoryKey);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void Rebuild()
    {
        var filter = SearchText.Trim();
        Items.Clear();

        IEnumerable<HistoryItemViewModel> items = _workspace.LastTerminal is { } terminal
            ? _history.GetForHost(terminal.HistoryKey).Select(e => new HistoryItemViewModel(terminal.HistoryKey, terminal.Host.DisplayName, e, this))
            : _history.GetRecent(GlobalLimit).Select(x => new HistoryItemViewModel(x.HostKey, x.HostName, x.Entry, this));

        foreach (var item in items)
        {
            if (filter.Length == 0 || item.Command.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                Items.Add(item);
        }

        OnPropertyChanged(nameof(IsHostScoped));
        OnPropertyChanged(nameof(IsGlobal));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(HasNoResults));
    }
}
