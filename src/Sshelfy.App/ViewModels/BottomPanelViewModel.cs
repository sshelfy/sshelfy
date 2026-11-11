using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.ViewModels.Tunnels;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels;

/// <summary>Docked bottom area: transfer queue and tunnels.</summary>
public sealed partial class BottomPanelViewModel : ViewModelBase
{
    private readonly TransferQueue _queue;
    private readonly Services.IShellIntegration _shell;

    public BottomPanelViewModel(TransferQueue queue, TunnelsPanelViewModel tunnels, Services.IShellIntegration shell, ActivityPanelViewModel activity)
    {
        Activity = activity;
        _queue = queue;
        _shell = shell;
        Tunnels = tunnels;
        IsTransfersPage = true;
        Transfers = new ObservableCollection<TransferItemViewModel>(queue.Items.Select(i => new TransferItemViewModel(i, _shell, queue)));

        queue.Items.CollectionChanged += OnItemsChanged;
        queue.Finished += (_, _) => RaiseSummary();
    }

    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    public TunnelsPanelViewModel Tunnels { get; }

    public ActivityPanelViewModel Activity { get; }

    [ObservableProperty]
    public partial bool IsActivityPage { get; set; }

    [ObservableProperty]
    public partial bool IsTransfersPage { get; set; }

    [ObservableProperty]
    public partial bool IsTunnelsPage { get; set; }

    public void ShowTunnels() => ShowPage(tunnels: true);

    public void ShowTransfers() => ShowPage(transfers: true);

    public void ShowActivity() => ShowPage(activity: true);

    private void ShowPage(bool transfers = false, bool tunnels = false, bool activity = false)
    {
        IsTransfersPage = transfers;
        IsTunnelsPage = tunnels;
        IsActivityPage = activity;
    }

    public bool HasTransfers => Transfers.Count > 0;

    public bool HasFinished => Transfers.Any(t => t.Item.IsFinished);

    public int ActiveCount => _queue.ActiveCount;

    /// <summary>Raised when a new transfer starts, so the shell can reveal the panel.</summary>
    public event EventHandler? TransferStarted;

    [RelayCommand]
    private void ClearFinished()
    {
        _queue.ClearFinished();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            var index = e.NewStartingIndex;
            foreach (TransferItem item in e.NewItems)
                Transfers.Insert(Math.Clamp(index++, 0, Transfers.Count), new TransferItemViewModel(item, _shell, _queue));
            IsTransfersPage = true;
            IsTunnelsPage = false;
            IsActivityPage = false;
            TransferStarted?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (TransferItem item in e.OldItems)
            {
                var vm = Transfers.FirstOrDefault(t => t.Item == item);
                if (vm is not null)
                    Transfers.Remove(vm);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Transfers.Clear();
        }

        RaiseSummary();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

/// <summary>Presentation of one transfer row.</summary>
public sealed partial class TransferItemViewModel : ViewModelBase
{
    private readonly Services.IShellIntegration _shell;
    private readonly TransferQueue _queue;

    public TransferItemViewModel(TransferItem item, Services.IShellIntegration shell, TransferQueue queue)
    {
        _shell = shell;
        _queue = queue;
        Item = item;
        item.PropertyChanged += OnItemChanged;
    }

    public TransferItem Item { get; }

    public string Name => Item.Name;

    public AppIcon Icon => Item.Direction == TransferDirection.Upload ? AppIcon.Upload : AppIcon.Download;

    public string Route => Item.Direction == TransferDirection.Upload
        ? $"to {Item.HostName}:{RemotePath.Parent(Item.RemotePath)}"
        : $"from {Item.HostName} to {Path.GetDirectoryName(Item.LocalPath)}";

    public double Progress => Item.Progress;

    public bool IsRunning => Item.State == TransferState.Running;

    public bool IsActive => Item.IsActive;

    public bool IsFailed => Item.State == TransferState.Failed;

    public bool IsCompleted => Item.State == TransferState.Completed;

    public bool CanReveal => IsCompleted && Item.Direction == TransferDirection.Download;

    public bool CanRetry => Item.CanRetry;

    public string Detail => Item.State switch
    {
        TransferState.Queued => "Waiting…",
        TransferState.Running => $"{ByteSize.Format(Item.TransferredBytes)} of {ByteSize.Format(Item.TotalBytes)}" +
                                 (Item.BytesPerSecond > 0 ? $"  ·  {ByteSize.Format((long)Item.BytesPerSecond)}/s" : string.Empty) +
                                 (Item.IsDirectory && Item.CurrentFile is { } file ? $"  ·  {file}" : string.Empty),
        TransferState.Completed => $"{ByteSize.Format(Item.TotalBytes)}  ·  Done",
        TransferState.Cancelled => "Cancelled",
        TransferState.Failed => Item.Error ?? "Failed",
        _ => string.Empty,
    };

    [RelayCommand]
    private void Cancel() => Item.Cancel();

    /// <summary>Starts the transfer again; finished files are skipped and partial ones resumed.</summary>
    [RelayCommand]
    private void Retry() => _queue.Retry(Item);

    /// <summary>Opens the system file manager with the downloaded item selected.</summary>
    [RelayCommand]
    private void Reveal() => _shell.Reveal(Item.LocalPath);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Detail));
        if (e.PropertyName == nameof(TransferItem.State))
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(CanRetry));
        }
    }
}
