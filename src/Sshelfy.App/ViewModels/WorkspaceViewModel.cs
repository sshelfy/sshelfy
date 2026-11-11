using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.ViewModels.Tabs;

namespace Sshelfy.App.ViewModels;

/// <summary>The document area: open tabs and the active one.</summary>
public sealed partial class WorkspaceViewModel : ViewModelBase
{
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTerminal))]
    public partial TabViewModel? ActiveTab { get; set; }

    /// <summary>The terminal the user worked in most recently (stays set while an SFTP tab is focused).</summary>
    [ObservableProperty]
    public partial TerminalTabViewModel? LastTerminal { get; private set; }

    /// <summary>The focused terminal: the active terminal tab, or the active pane of a split tab.</summary>
    public TerminalTabViewModel? ActiveTerminal => ActiveTab switch
    {
        TerminalTabViewModel terminal => terminal,
        SplitTabViewModel split => split.ActivePane,
        _ => null,
    };

    /// <summary>Every open terminal session, including the panes of split tabs.</summary>
    public IEnumerable<TerminalTabViewModel> Terminals => Tabs.SelectMany(t => t switch
    {
        TerminalTabViewModel terminal => [terminal],
        SplitTabViewModel split => split.Panes,
        _ => Enumerable.Empty<TerminalTabViewModel>(),
    });

    public event EventHandler<TabViewModel?>? ActiveTabChanged;

    /// <summary>A terminal session joined the workspace (as a tab or as a pane).</summary>
    public event EventHandler<TerminalTabViewModel>? TerminalAdded;

    /// <summary>A terminal session left the workspace for good.</summary>
    public event EventHandler<TerminalTabViewModel>? TerminalRemoved;

    public void Open(TabViewModel tab)
    {
        Tabs.Add(tab);
        if (tab is TerminalTabViewModel terminal)
            TerminalAdded?.Invoke(this, terminal);
        ActiveTab = tab;
    }

    public T? Find<T>(Func<T, bool> predicate) where T : TabViewModel => Tabs.OfType<T>().FirstOrDefault(predicate);

    /// <summary>Brings a terminal to the front, whether it is a tab or a pane of a split tab.</summary>
    public void Activate(TerminalTabViewModel terminal)
    {
        if (SplitOf(terminal) is { } split)
        {
            split.ActivatePane(terminal);
            ActiveTab = split;
        }
        else
        {
            ActiveTab = terminal;
        }
    }

    /// <summary>The split tab holding <paramref name="pane"/>, if it is a pane.</summary>
    public SplitTabViewModel? SplitOf(TerminalTabViewModel pane) => Tabs.OfType<SplitTabViewModel>().FirstOrDefault(s => s.Contains(pane));

    /// <summary>Shows <paramref name="pane"/> next to <paramref name="target"/>, turning a plain terminal tab into a split tab.</summary>
    public void Split(TerminalTabViewModel target, TerminalTabViewModel pane, bool sideBySide)
    {
        var split = SplitOf(target);
        if (split is null)
        {
            var index = Tabs.IndexOf(target);
            if (index < 0)
                return;

            split = new SplitTabViewModel(target);
            split.ActivePaneChanged += OnActivePaneChanged;
            target.IsActive = false;
            Tabs[index] = split;
        }

        split.Split(target, pane, sideBySide);
        TerminalAdded?.Invoke(this, pane);
        if (ActiveTab == split)
            OnActivePaneChanged(split, EventArgs.Empty);
        else
            ActiveTab = split;
    }

    [RelayCommand]
    public async Task CloseTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;

        if (tab is TerminalTabViewModel pane && SplitOf(pane) is { } owner)
        {
            await ClosePaneAsync(owner, pane);
            return;
        }

        if (tab is null || !tab.CanClose || !await tab.ConfirmCloseAsync())
            return;

        var index = Tabs.IndexOf(tab);
        var terminals = TerminalsOf(tab).ToList();
        Tabs.Remove(tab);
        foreach (var terminal in terminals)
            TerminalRemoved?.Invoke(this, terminal);
        if (LastTerminal is not null && terminals.Contains(LastTerminal))
            LastTerminal = Terminals.LastOrDefault();
        if (ActiveTab == tab || ActiveTab is null)
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];

        await tab.DisposeAsync();
    }

    /// <summary>Closes the focused pane of a split tab (or the tab itself when it is not split).</summary>
    [RelayCommand]
    private Task CloseActive() =>
        ActiveTab is SplitTabViewModel split ? ClosePaneAsync(split, split.ActivePane) : CloseTab(null);

    [RelayCommand]
    private async Task CloseOtherTabs(TabViewModel? keep)
    {
        foreach (var tab in Tabs.Where(t => t != keep && t.CanClose).ToList())
            await CloseTab(tab);
    }

    [RelayCommand]
    private void NextTab() => Cycle(1);

    [RelayCommand]
    private void PreviousTab() => Cycle(-1);

    public async Task CloseAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            Tabs.Remove(tab);
            foreach (var terminal in TerminalsOf(tab))
                TerminalRemoved?.Invoke(this, terminal);
            await tab.DisposeAsync();
        }
    }

    private async Task ClosePaneAsync(SplitTabViewModel split, TerminalTabViewModel pane)
    {
        split.Remove(pane);
        TerminalRemoved?.Invoke(this, pane);

        // A split with one pane left goes back to being a plain terminal tab.
        if (split.Panes.Count == 1)
        {
            var remaining = split.Panes[0];
            var index = Tabs.IndexOf(split);
            var wasActive = ActiveTab == split;
            split.ActivePaneChanged -= OnActivePaneChanged;
            split.Release(remaining);
            Tabs[index] = remaining;
            if (wasActive || ActiveTab is null)
                ActiveTab = remaining;
        }
        else if (ActiveTab == split)
        {
            OnActivePaneChanged(split, EventArgs.Empty);
        }

        if (LastTerminal == pane)
            LastTerminal = ActiveTerminal ?? Terminals.LastOrDefault();

        await pane.DisposeAsync();
    }

    private static IEnumerable<TerminalTabViewModel> TerminalsOf(TabViewModel tab) => tab switch
    {
        TerminalTabViewModel terminal => [terminal],
        SplitTabViewModel split => split.Panes.ToList(),
        _ => [],
    };

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsActive = false;
        if (newValue is not null)
            newValue.IsActive = true;
        if (ActiveTerminal is { } terminal)
            LastTerminal = terminal;
        ActiveTabChanged?.Invoke(this, newValue);
    }

    private void OnActivePaneChanged(object? sender, EventArgs e)
    {
        if (sender != ActiveTab)
            return;
        OnPropertyChanged(nameof(ActiveTerminal));
        if (ActiveTerminal is { } terminal)
            LastTerminal = terminal;
        ActiveTabChanged?.Invoke(this, ActiveTab);
    }

    private void Cycle(int direction)
    {
        if (Tabs.Count < 2 || ActiveTab is null)
            return;
        var index = (Tabs.IndexOf(ActiveTab) + direction + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }
}
