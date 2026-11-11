using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Snippets;

namespace Sshelfy.App.ViewModels.Sidebar;

public sealed class SnippetItemViewModel(Snippet snippet, SnippetsSidebarViewModel owner) : ViewModelBase
{
    public Snippet Snippet { get; } = snippet;

    public SnippetsSidebarViewModel Owner { get; } = owner;

    public string Name => Snippet.Name;

    /// <summary>First line only; multi-line scripts get an ellipsis.</summary>
    public string Preview
    {
        get
        {
            var lines = Snippet.Command.Split('\n');
            return lines.Length > 1 ? lines[0].TrimEnd('\r') + " …" : Snippet.Command;
        }
    }

    public bool RunImmediately => Snippet.RunImmediately;
}

/// <summary>Library of saved commands that can be sent to the current terminal.</summary>
public sealed partial class SnippetsSidebarViewModel : SidebarSectionViewModel
{
    private readonly ISnippetLibrary _library;
    private readonly IDialogService _dialogs;
    private readonly ITerminalCommandSender _sender;
    private readonly IClipboardService _clipboard;
    private readonly StatusBarViewModel _status;

    public SnippetsSidebarViewModel(
        ISnippetLibrary library,
        IDialogService dialogs,
        ITerminalCommandSender sender,
        IClipboardService clipboard,
        StatusBarViewModel status)
    {
        _library = library;
        _dialogs = dialogs;
        _sender = sender;
        _clipboard = clipboard;
        _status = status;

        _library.Changed += (_, _) => Rebuild();
        _sender.TargetChanged += (_, _) => OnPropertyChanged(nameof(TargetText));
        Rebuild();
    }

    public override string Title => "Snippets";

    public override AppIcon Icon => AppIcon.CodeXml;

    public ObservableCollection<SnippetItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SnippetItemViewModel? SelectedItem { get; set; }

    public bool IsLibraryEmpty => _library.Snippets.Count == 0;

    public bool HasNoResults => !IsLibraryEmpty && Items.Count == 0;

    public string TargetText => _sender.TargetName is { } name ? $"Runs in {name}" : "Open a terminal to run snippets";

    partial void OnSearchTextChanged(string value) => Rebuild();

    [RelayCommand]
    private async Task New()
    {
        var snippet = await _dialogs.ShowAsync(new SnippetEditorDialogViewModel(null));
        if (snippet is not null)
            await _library.SaveAsync(snippet);
    }

    /// <summary>Creates a snippet pre-filled with a command (used from the history panel).</summary>
    public async Task CreateFromCommandAsync(string command)
    {
        var snippet = await _dialogs.ShowAsync(new SnippetEditorDialogViewModel(new Snippet { Command = command }, isNew: true));
        if (snippet is not null)
        {
            await _library.SaveAsync(snippet);
            _status.Set($"Saved snippet \"{snippet.Name}\"", StatusLevel.Ok);
        }
    }

    [RelayCommand]
    private async Task Edit(SnippetItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;
        var snippet = await _dialogs.ShowAsync(new SnippetEditorDialogViewModel(item.Snippet));
        if (snippet is not null)
            await _library.SaveAsync(snippet);
    }

    [RelayCommand]
    private async Task Delete(SnippetItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null && await _dialogs.ConfirmAsync("Delete snippet", $"Delete \"{item.Name}\"?", "Delete", isDestructive: true))
            await _library.DeleteAsync(item.Snippet.Id);
    }

    /// <summary>Sends the snippet to the terminal, pressing Enter if the snippet says so.</summary>
    [RelayCommand]
    private async Task Run(SnippetItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null)
            await RunSnippetAsync(item.Snippet);
    }

    /// <summary>Sends a snippet to the current terminal (also used by the command palette).</summary>
    public Task RunSnippetAsync(Snippet snippet) => SendAsync(snippet, snippet.RunImmediately);

    /// <summary>Types the snippet without pressing Enter, so it can be edited first.</summary>
    [RelayCommand]
    private async Task Insert(SnippetItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null)
            await SendAsync(item.Snippet, run: false);
    }

    /// <summary>Fills in <c>{{variables}}</c> (asking for their values) and sends the result.</summary>
    private async Task SendAsync(Snippet snippet, bool run)
    {
        if (!_sender.HasTarget)
        {
            _sender.Send(snippet.Command, run); // reports the missing terminal
            return;
        }

        var command = snippet.Command;
        IReadOnlyDictionary<string, string>? values = null;
        if (SnippetTemplate.HasVariables(command))
        {
            values = await _dialogs.ShowAsync(new SnippetVariablesDialogViewModel(snippet, run));
            if (values is null)
                return;
            command = SnippetTemplate.Fill(command, values);
        }

        if (_sender.Send(command, run))
            await _library.MarkUsedAsync(snippet.Id, values);
    }

    [RelayCommand]
    private async Task Copy(SnippetItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;
        await _clipboard.SetTextAsync(item.Snippet.Command);
        _status.Set($"Copied \"{item.Name}\"");
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void Rebuild()
    {
        var filter = SearchText.Trim();
        Items.Clear();
        foreach (var snippet in _library.Snippets
                     .Where(s => filter.Length == 0
                                 || s.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                 || s.Command.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                     .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(new SnippetItemViewModel(snippet, this));
        }

        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(HasNoResults));
    }
}
