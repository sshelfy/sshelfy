using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Tabs;

/// <summary>A node of a split layout: one pane (terminal or SFTP), or two nodes side by side / stacked.</summary>
public abstract class PaneNode : ViewModelBase;

public sealed class PaneLeaf(TabViewModel pane, SplitTabViewModel owner) : PaneNode
{
    public TabViewModel Pane { get; } = pane;

    public SplitTabViewModel Owner { get; } = owner;
}

public abstract partial class PaneSplit(PaneNode first, PaneNode second) : PaneNode
{
    [ObservableProperty]
    public partial PaneNode First { get; set; } = first;

    [ObservableProperty]
    public partial PaneNode Second { get; set; } = second;
}

/// <summary>Panes next to each other (a vertical divider).</summary>
public sealed class ColumnsSplit(PaneNode first, PaneNode second) : PaneSplit(first, second);

/// <summary>Panes above each other (a horizontal divider).</summary>
public sealed class RowsSplit(PaneNode first, PaneNode second) : PaneSplit(first, second);

/// <summary>
/// A tab showing several sessions at once. Each pane is a full tab (a terminal or an SFTP browser);
/// the active pane receives keyboard focus and stands in for the tab (title, status, recording).
/// In broadcast mode keyboard input typed in one terminal pane is replayed in every other connected terminal pane.
/// </summary>
public sealed partial class SplitTabViewModel : TabViewModel
{
    public SplitTabViewModel(TabViewModel first)
    {
        Root = new PaneLeaf(first, this);
        Attach(first);
        ActivePane = first;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsActive))
                UpdatePaneActivity();
        };
    }

    public ObservableCollection<TabViewModel> Panes { get; } = [];

    [ObservableProperty]
    public partial PaneNode Root { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetail), nameof(IsRecording), nameof(ServerStats))]
    public partial TabViewModel ActivePane { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BroadcastText))]
    public partial bool IsBroadcast { get; set; }

    public string BroadcastText => $"Typing goes to all {Terminals.Count()} terminals in this tab";

    public IEnumerable<TerminalTabViewModel> Terminals => Panes.OfType<TerminalTabViewModel>();

    public override AppIcon Icon => AppIcon.Columns2;

    public override bool IsRecording => ActivePane.IsRecording;

    public override string? ServerStats => ActivePane.ServerStats;

    public override string? StatusDetail => ActivePane.StatusDetail is { } detail
        ? $"{(IsBroadcast ? "BROADCAST  ·  " : string.Empty)}Pane {Panes.IndexOf(ActivePane) + 1} of {Panes.Count}  ·  {detail}"
        : null;

    /// <summary>Raised when the active pane changed, so the workspace can follow it.</summary>
    public event EventHandler? ActivePaneChanged;

    [RelayCommand]
    public void ActivatePane(TabViewModel? pane)
    {
        if (pane is not null && Panes.Contains(pane))
            ActivePane = pane;
    }

    [RelayCommand]
    private void ToggleBroadcast() => IsBroadcast = !IsBroadcast;

    /// <summary>Puts <paramref name="pane"/> next to (or below) <paramref name="target"/> and makes it active.</summary>
    public void Split(TabViewModel target, TabViewModel pane, bool sideBySide)
    {
        if (FindLeaf(Root, target) is not { } leaf)
            return;

        var added = new PaneLeaf(pane, this);
        PaneNode split = sideBySide ? new ColumnsSplit(leaf, added) : new RowsSplit(leaf, added);
        Replace(leaf, split);
        Attach(pane);
        ActivePane = pane;
        OnPropertyChanged(nameof(BroadcastText));
    }

    /// <summary>Takes a pane out of the layout (its sibling fills the space). The caller disposes it.</summary>
    public void Remove(TabViewModel pane)
    {
        if (FindLeaf(Root, pane) is not { } leaf || FindParent(Root, leaf) is not { } parent)
            return;

        var sibling = parent.First == leaf ? parent.Second : parent.First;
        Replace(parent, sibling);
        Detach(pane);
        pane.IsActive = false;
        if (ActivePane == pane)
            ActivePane = FirstPane(sibling);
        OnPropertyChanged(nameof(BroadcastText));
        OnPropertyChanged(nameof(StatusDetail));
    }

    /// <summary>Hands the last pane back so it can become a plain tab again; the split is empty afterwards.</summary>
    public void Release(TabViewModel pane)
    {
        Detach(pane);
        pane.IsActive = false;
        IsBroadcast = false;
    }

    public bool Contains(TabViewModel pane) => Panes.Contains(pane);

    partial void OnActivePaneChanged(TabViewModel value)
    {
        UpdatePaneActivity();
        UpdateTitle();
        ActivePaneChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsBroadcastChanged(bool value) => OnPropertyChanged(nameof(StatusDetail));

    public override async ValueTask DisposeAsync()
    {
        foreach (var pane in Panes.ToList())
        {
            Detach(pane);
            await pane.DisposeAsync();
        }
    }

    private void Attach(TabViewModel pane)
    {
        Panes.Add(pane);
        if (pane is TerminalTabViewModel terminal)
            terminal.Terminal.InputSent += input => OnInput(terminal, input);
        pane.PropertyChanged += OnPanePropertyChanged;
        UpdateTitle();
    }

    private void Detach(TabViewModel pane)
    {
        Panes.Remove(pane);
        pane.PropertyChanged -= OnPanePropertyChanged;
        UpdateTitle();
    }

    private void OnInput(TerminalTabViewModel source, TerminalInput input)
    {
        if (!IsBroadcast || !Panes.Contains(source))
            return;

        foreach (var pane in Terminals)
        {
            if (pane != source && pane.IsConnected)
                pane.Terminal.Replay(input);
        }
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Title))
            UpdateTitle();

        // A pane in the background of an inactive split tab marks the tab itself.
        if (e.PropertyName == nameof(HasAlert) && sender is TabViewModel { HasAlert: true } && !IsActive)
            HasAlert = true;

        if (sender != ActivePane)
            return;
        if (e.PropertyName == nameof(StatusDetail))
            OnPropertyChanged(nameof(StatusDetail));
        else if (e.PropertyName == nameof(IsRecording))
            OnPropertyChanged(nameof(IsRecording));
        else if (e.PropertyName == nameof(ServerStats))
            OnPropertyChanged(nameof(ServerStats));
    }

    private void UpdatePaneActivity()
    {
        foreach (var pane in Panes)
            pane.IsActive = IsActive && pane == ActivePane;
    }

    private void UpdateTitle()
    {
        if (ActivePane is null)
            return;
        Title = Panes.Count > 1 ? $"{ActivePane.Title} +{Panes.Count - 1}" : ActivePane.Title;
        Subtitle = string.Join("  |  ", Panes.Select(p => p.Subtitle ?? p.Title));
    }

    private void Replace(PaneNode node, PaneNode replacement)
    {
        if (Root == node)
        {
            Root = replacement;
            return;
        }

        if (FindParent(Root, node) is { } parent)
        {
            if (parent.First == node)
                parent.First = replacement;
            else
                parent.Second = replacement;
        }
    }

    private static PaneLeaf? FindLeaf(PaneNode node, TabViewModel pane) => node switch
    {
        PaneLeaf leaf when leaf.Pane == pane => leaf,
        PaneSplit split => FindLeaf(split.First, pane) ?? FindLeaf(split.Second, pane),
        _ => null,
    };

    private static PaneSplit? FindParent(PaneNode node, PaneNode child)
    {
        if (node is not PaneSplit split)
            return null;
        if (split.First == child || split.Second == child)
            return split;
        return FindParent(split.First, child) ?? FindParent(split.Second, child);
    }

    private static TabViewModel FirstPane(PaneNode node) => node switch
    {
        PaneLeaf leaf => leaf.Pane,
        PaneSplit split => FirstPane(split.First),
        _ => throw new InvalidOperationException(),
    };
}
