using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.ViewModels.Tunnels;

/// <summary>One port forward of a terminal session (saved with the host or added just for this session).</summary>
public sealed partial class TunnelViewModel : ViewModelBase
{
    private readonly TerminalTabViewModel _session;
    private IPortForward? _forward;

    public TunnelViewModel(TerminalTabViewModel session, PortForwardRule rule, bool isTemporary)
    {
        _session = session;
        Rule = rule;
        IsTemporary = isTemporary;
    }

    public PortForwardRule Rule { get; }

    /// <summary>Added during the session only; disappears when the tab closes.</summary>
    public bool IsTemporary { get; }

    public string HostName => _session.Host.DisplayName;

    public string Description => Rule.Describe();

    public string KindLabel => Rule.Kind switch
    {
        ForwardKind.Local => "Local",
        ForwardKind.Remote => "Remote",
        _ => "SOCKS",
    };

    public AppIcon Icon => Rule.Kind switch
    {
        ForwardKind.Remote => AppIcon.Download,
        ForwardKind.Dynamic => AppIcon.Zap,
        _ => AppIcon.Upload,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsFailed), nameof(Detail))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial TunnelState State { get; private set; } = TunnelState.Stopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial int ConnectionCount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsActive => State == TunnelState.Active;

    public bool IsFailed => State == TunnelState.Failed;

    public string Detail => State switch
    {
        TunnelState.Active when Error is not null => $"Active  ·  last error: {Error}",
        TunnelState.Active => ConnectionCount == 1 ? "Active  ·  1 connection" : $"Active  ·  {ConnectionCount} connections",
        TunnelState.Failed => Error ?? "Failed",
        _ => _session.IsConnected ? "Stopped" : "Waiting for the session",
    };

    private bool CanStart => !IsBusy && State != TunnelState.Active && _session.IsConnected;

    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task Start() => _session.StartTunnelAsync(this);

    private bool CanStop => !IsBusy && State == TunnelState.Active;

    [RelayCommand(CanExecute = nameof(CanStop))]
    public Task Stop() => StopAsync();

    internal async Task StartOnAsync(ISshConnection connection)
    {
        IsBusy = true;
        Error = null;
        try
        {
            var forward = await connection.StartForwardAsync(Rule);
            forward.ConnectionOpened += (_, _) => Dispatcher.UIThread.Post(() => ConnectionCount = forward.ConnectionCount);
            forward.Failed += (_, message) => Dispatcher.UIThread.Post(() => Error = message);
            _forward = forward;
            ConnectionCount = 0;
            State = TunnelState.Active;
            _session.LogActivity(Sshelfy.Core.Activity.ActivityKind.TunnelStarted, $"Tunnel {Description}");
        }
        catch (PortForwardException ex)
        {
            Error = ex.Message;
            State = TunnelState.Failed;
            _session.LogActivity(Sshelfy.Core.Activity.ActivityKind.TunnelFailed, $"Tunnel {Description}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task StopAsync()
    {
        if (_forward is { } forward)
        {
            _forward = null;
            await forward.DisposeAsync();
        }

        if (State == TunnelState.Active)
            State = TunnelState.Stopped;
    }

    /// <summary>The session's connection went away; the forward died with it.</summary>
    internal void MarkSessionEnded()
    {
        _forward = null;
        if (State == TunnelState.Active)
            State = TunnelState.Stopped;
        OnPropertyChanged(nameof(Detail));
        StartCommand.NotifyCanExecuteChanged();
    }

    internal void RefreshAvailability()
    {
        OnPropertyChanged(nameof(Detail));
        StartCommand.NotifyCanExecuteChanged();
    }
}
