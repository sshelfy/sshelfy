using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.Services;

/// <summary>
/// Turns the open tabs (with their split panes) into <see cref="SavedTab"/>s and back. Used for named
/// layouts and for reopening the last session. Only saved hosts and local shells are kept: ad-hoc
/// connections have nothing to reconnect to.
/// </summary>
public sealed class LayoutService(WorkspaceViewModel workspace, ISessionLauncher launcher, IHostCatalog catalog, ILocalShellLauncher localShells)
{
    public IReadOnlyList<SavedTab> Capture() => workspace.Tabs.Select(Capture).OfType<SavedTab>().ToList();

    private SavedTab? Capture(TabViewModel tab) => tab switch
    {
        SplitTabViewModel split => CapturePane(split.Root) switch
        {
            null => null,
            { Tab: { } single } => single,
            var tree => new SavedTab(SavedTabKind.Split, Guid.Empty, Panes: tree),
        },
        TerminalTabViewModel terminal => Leaf(terminal),
        SftpTabViewModel sftp when catalog.FindHost(sftp.Host.Id) is not null => new SavedTab(SavedTabKind.Sftp, sftp.Host.Id),
        _ => null,
    };

    /// <summary>A pane that cannot be saved drops out and its sibling takes the space.</summary>
    private SavedPane? CapturePane(PaneNode node) => node switch
    {
        PaneLeaf leaf => Leaf(leaf.Pane) is { } tab ? new SavedPane(tab) : null,
        PaneSplit split => (CapturePane(split.First), CapturePane(split.Second)) switch
        {
            (null, var second) => second,
            (var first, null) => first,
            var (first, second) => new SavedPane(SideBySide: split is ColumnsSplit, First: first, Second: second),
        },
        _ => null,
    };

    private SavedTab? Leaf(TerminalTabViewModel terminal) => terminal switch
    {
        { IsExternalSsh: true } or { LocalShell: null } when catalog.FindHost(terminal.Host.Id) is not null => new SavedTab(SavedTabKind.Terminal, terminal.Host.Id),
        { IsExternalSsh: false, LocalShell: { } local } => new SavedTab(SavedTabKind.Local, Guid.Empty, local.Id),
        _ => null,
    };

    /// <summary>Opens the tabs, rebuilding splits, then connects the sessions one by one.</summary>
    /// <returns>How many tabs and panes were opened (saved hosts that were deleted since are skipped).</returns>
    public async Task<int> OpenAsync(IReadOnlyList<SavedTab> tabs)
    {
        var shells = tabs.Any(t => t.Kind == SavedTabKind.Local || t.Panes?.Leaves().Any(l => l.Kind == SavedTabKind.Local) == true) && localShells.IsSupported
            ? await localShells.DetectAsync()
            : [];

        var opened = 0;
        foreach (var saved in tabs)
        {
            if (saved.Kind == SavedTabKind.Sftp)
            {
                if (catalog.FindHost(saved.HostId) is { } host)
                {
                    await launcher.OpenSftpAsync(host);
                    opened++;
                }

                continue;
            }

            var panes = new List<TerminalTabViewModel>();
            var tree = saved.Panes ?? new SavedPane(saved);
            if (FirstAvailable(tree, shells) is not { } firstLeaf || Create(firstLeaf, shells) is not { } first)
                continue;

            workspace.Open(first);
            panes.Add(first);
            Build(tree, first, panes, shells);

            foreach (var pane in panes)
                await pane.ConnectAsync();
            opened += panes.Count;
        }

        return opened;
    }

    /// <summary>Lays out <paramref name="node"/> in the place of <paramref name="anchor"/>, the terminal of its first available leaf.</summary>
    private void Build(SavedPane node, TerminalTabViewModel anchor, List<TerminalTabViewModel> panes, IReadOnlyList<LocalShellProfile> shells)
    {
        if (node.Tab is not null || node.First is null || node.Second is null)
            return;

        if (FirstAvailable(node.First, shells) is null)
        {
            Build(node.Second, anchor, panes, shells);
            return;
        }

        if (FirstAvailable(node.Second, shells) is not { } secondLeaf || Create(secondLeaf, shells) is not { } second)
        {
            Build(node.First, anchor, panes, shells);
            return;
        }

        // Split the anchor first, then grow each half inside its own place.
        workspace.Split(anchor, second, node.SideBySide);
        panes.Add(second);
        Build(node.First, anchor, panes, shells);
        Build(node.Second, second, panes, shells);
    }

    private SavedTab? FirstAvailable(SavedPane node, IReadOnlyList<LocalShellProfile> shells) =>
        node.Leaves().FirstOrDefault(leaf => IsAvailable(leaf, shells));

    private bool IsAvailable(SavedTab leaf, IReadOnlyList<LocalShellProfile> shells) => leaf.Kind switch
    {
        SavedTabKind.Local => shells.Any(s => s.Id == leaf.LocalShellId),
        SavedTabKind.Terminal => catalog.FindHost(leaf.HostId) is not null,
        _ => false,
    };

    private TerminalTabViewModel? Create(SavedTab leaf, IReadOnlyList<LocalShellProfile> shells) => leaf.Kind switch
    {
        SavedTabKind.Local when shells.FirstOrDefault(s => s.Id == leaf.LocalShellId) is { } shell => launcher.CreateLocalTab(shell),
        SavedTabKind.Terminal when catalog.FindHost(leaf.HostId) is { } host => launcher.CreateTerminalTab(host),
        _ => null,
    };
}
