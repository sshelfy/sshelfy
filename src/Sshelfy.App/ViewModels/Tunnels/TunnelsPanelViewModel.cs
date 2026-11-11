using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Hosts;

namespace Sshelfy.App.ViewModels.Tunnels;

/// <summary>All tunnels of all open terminal sessions, for the bottom panel.</summary>
public sealed partial class TunnelsPanelViewModel : ViewModelBase
{
    private readonly WorkspaceViewModel _workspace;
    private readonly IDialogService _dialogs;
    private readonly IHostCatalog _catalog;
    private readonly StatusBarViewModel _status;

    public TunnelsPanelViewModel(WorkspaceViewModel workspace, IDialogService dialogs, IHostCatalog catalog, StatusBarViewModel status)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        _catalog = catalog;
        _status = status;

        _workspace.TerminalAdded += (_, tab) =>
        {
            tab.Tunnels.CollectionChanged += OnTunnelsChanged;
            Rebuild();
        };
        _workspace.TerminalRemoved += (_, tab) =>
        {
            tab.Tunnels.CollectionChanged -= OnTunnelsChanged;
            Rebuild();
        };
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.LastTerminal))
            {
                OnPropertyChanged(nameof(CanAdd));
                OnPropertyChanged(nameof(AddHint));
                AddCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public ObservableCollection<TunnelViewModel> Items { get; } = [];

    public bool HasTunnels => Items.Count > 0;

    public int ActiveCount => Items.Count(t => t.IsActive);

    public bool CanAdd => _workspace.LastTerminal is { IsLocal: false };

    public string AddHint => _workspace.LastTerminal is { IsLocal: false } terminal ? $"New tunnel for {terminal.Host.DisplayName}" : "Open an SSH session to add a tunnel";

    /// <summary>Adds a tunnel to the most recently used terminal session.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task Add()
    {
        if (_workspace.LastTerminal is not { } terminal)
            return;

        var isSavedHost = _catalog.FindHost(terminal.Host.Id) is not null;
        var result = await _dialogs.ShowAsync(new PortForwardEditorDialogViewModel(null, offerSaveToHost: isSavedHost));
        if (result is null)
            return;

        if (result.SaveToHost && _catalog.FindHost(terminal.Host.Id) is { } host)
            await _catalog.SaveHostAsync(host with { PortForwards = [.. host.PortForwards, result.Rule] });

        await terminal.AddTunnelAsync(result.Rule, isTemporary: !result.SaveToHost);
    }

    [RelayCommand]
    private async Task Remove(TunnelViewModel? tunnel)
    {
        if (tunnel is null)
            return;

        var session = FindSession(tunnel);
        if (session is null)
            return;

        if (!tunnel.IsTemporary)
        {
            _status.Set("Saved tunnels are removed in the host editor; stopping it instead.");
            await tunnel.StopAsync();
            return;
        }

        await session.RemoveTunnelAsync(tunnel);
    }

    private TerminalTabViewModel? FindSession(TunnelViewModel tunnel) =>
        _workspace.Terminals.FirstOrDefault(t => t.Tunnels.Contains(tunnel));

    private void OnTunnelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnTunnelChanged;
        Items.Clear();

        foreach (var tunnel in _workspace.Terminals.SelectMany(t => t.Tunnels))
        {
            tunnel.PropertyChanged += OnTunnelChanged;
            Items.Add(tunnel);
        }

        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(AddHint));
    }

    private void OnTunnelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TunnelViewModel.State))
            OnPropertyChanged(nameof(ActiveCount));
    }
}
