using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Sidebar;
using Sshelfy.Core.History;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Search;
using Sshelfy.Core.Snippets;

namespace Sshelfy.App.ViewModels;

/// <summary>An app command offered by the palette ("New host", "Toggle theme"…).</summary>
public sealed record PaletteAction(string Name, AppIcon Icon, Func<Task> Execute, string? Shortcut = null, Func<bool>? IsAvailable = null);

/// <summary>One row of the palette.</summary>
public sealed class PaletteItem(string category, string title, string? detail, AppIcon icon, Func<Task> execute, Func<Task>? alternate = null, string? shortcut = null)
    : ViewModelBase
{
    public string Category { get; } = category;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    public AppIcon Icon { get; } = icon;

    public string? Shortcut { get; } = shortcut;

    public Func<Task> Execute { get; } = execute;

    /// <summary>Ctrl+Enter: for hosts, open the file browser instead of a terminal.</summary>
    public Func<Task>? Alternate { get; } = alternate;

    internal int Score { get; init; }
}

/// <summary>
/// Ctrl+Shift+P: one box to jump to hosts, run snippets, reuse recent commands and invoke app
/// actions. Results are ranked with fuzzy matching; a leading "&gt;" limits the list to actions.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private const int MaxResults = 60;

    private readonly IHostCatalog _catalog;
    private readonly ISnippetLibrary _snippets;
    private readonly ICommandHistory _history;
    private readonly ISessionLauncher _launcher;
    private readonly ITerminalCommandSender _sender;
    private readonly SnippetsSidebarViewModel _snippetRunner;
    private IReadOnlyList<PaletteAction> _actions = [];

    public CommandPaletteViewModel(
        IHostCatalog catalog,
        ISnippetLibrary snippets,
        ICommandHistory history,
        ISessionLauncher launcher,
        ITerminalCommandSender sender,
        SnippetsSidebarViewModel snippetRunner)
    {
        _catalog = catalog;
        _snippets = snippets;
        _history = history;
        _launcher = launcher;
        _sender = sender;
        _snippetRunner = snippetRunner;
    }

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PaletteItem? SelectedItem { get; set; }

    /// <summary>Changes whenever the search field should take focus.</summary>
    [ObservableProperty]
    public partial int FocusRequest { get; private set; }

    public bool HasNoResults => Items.Count == 0;

    public string Hint => _sender.HasTarget
        ? $"Enter to run  ·  Ctrl+Enter opens files for a host  ·  commands go to {_sender.TargetName}"
        : "Enter to run  ·  Ctrl+Enter opens files for a host  ·  type > for actions only";

    /// <summary>App actions are owned by the shell; it hands them over once.</summary>
    public void SetActions(IReadOnlyList<PaletteAction> actions) => _actions = actions;

    [RelayCommand]
    public void Open()
    {
        Query = string.Empty;
        Refresh();
        IsOpen = true;
        FocusRequest++;
        OnPropertyChanged(nameof(Hint));
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private Task Run(PaletteItem? item) => Invoke(item ?? SelectedItem, alternate: false);

    [RelayCommand]
    private Task RunAlternate() => Invoke(SelectedItem, alternate: true);

    partial void OnQueryChanged(string value) => Refresh();

    private async Task Invoke(PaletteItem? item, bool alternate)
    {
        if (item is null)
            return;
        Close();
        await (alternate && item.Alternate is { } other ? other() : item.Execute());
    }

    private void Move(int delta)
    {
        if (Items.Count == 0)
            return;
        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[(index + delta + Items.Count) % Items.Count];
    }

    private void Refresh()
    {
        var query = Query.Trim();
        var actionsOnly = query.StartsWith('>');
        if (actionsOnly)
            query = query[1..].Trim();

        var results = new List<PaletteItem>();
        if (!actionsOnly)
        {
            results.AddRange(Hosts(query));
            results.AddRange(Snippets(query));
            if (query.Length > 0)
                results.AddRange(History(query));
        }

        results.AddRange(Actions(query));

        // With an empty query keep the natural order (hosts, snippets, actions); otherwise rank.
        IEnumerable<PaletteItem> ordered = query.Length == 0 ? results : results.OrderByDescending(r => r.Score);

        Items.Clear();
        foreach (var item in ordered.Take(MaxResults))
            Items.Add(item);
        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    private IEnumerable<PaletteItem> Hosts(string query)
    {
        var hosts = _catalog.Hosts.AsEnumerable();
        if (query.Length == 0)
            hosts = hosts.OrderByDescending(h => h.LastConnectedAt ?? DateTimeOffset.MinValue).Take(8);

        foreach (var host in hosts)
        {
            var score = Best(query, host.DisplayName, host.Endpoint);
            if (score is null)
                continue;
            yield return new PaletteItem("Hosts", host.DisplayName, host.Endpoint, AppIcon.Server,
                () => _launcher.ConnectAsync(host), () => _launcher.OpenSftpAsync(host)) { Score = score.Value + 6 };
        }
    }

    private IEnumerable<PaletteItem> Snippets(string query)
    {
        var snippets = _snippets.Snippets.AsEnumerable();
        if (query.Length == 0)
            snippets = snippets.OrderByDescending(s => s.LastUsedAt ?? DateTimeOffset.MinValue).Take(5);

        foreach (var snippet in snippets)
        {
            var score = Best(query, snippet.Name, snippet.Command);
            if (score is null)
                continue;
            var preview = snippet.Command.Split('\n')[0].TrimEnd('\r');
            yield return new PaletteItem("Snippets", snippet.Name, preview, AppIcon.CodeXml,
                () => _snippetRunner.RunSnippetAsync(snippet)) { Score = score.Value + 3 };
        }
    }

    private IEnumerable<PaletteItem> History(string query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, hostName, entry) in _history.GetRecent(300))
        {
            if (!seen.Add(entry.Command) || FuzzyMatcher.Score(query, entry.Command) is not { } score)
                continue;
            var command = entry.Command;
            yield return new PaletteItem("Recent commands", command, $"{hostName}  ·  inserts without running", AppIcon.History,
                () => Task.FromResult(_sender.Send(command, run: false))) { Score = score };
        }
    }

    private IEnumerable<PaletteItem> Actions(string query)
    {
        foreach (var action in _actions)
        {
            if (action.IsAvailable is { } available && !available())
                continue;
            if (FuzzyMatcher.Score(query, action.Name) is not { } score)
                continue;
            yield return new PaletteItem("Actions", action.Name, null, action.Icon, action.Execute, shortcut: action.Shortcut) { Score = score };
        }
    }

    private static int? Best(string query, string primary, string secondary)
    {
        var a = FuzzyMatcher.Score(query, primary);
        var b = FuzzyMatcher.Score(query, secondary) is { } s ? s - 4 : (int?)null;
        return a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }
}
