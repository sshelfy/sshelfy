using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed record PortForwardEditResult(PortForwardRule Rule, bool SaveToHost);

/// <summary>Creates or edits a port forwarding rule.</summary>
public sealed partial class PortForwardEditorDialogViewModel : DialogViewModel<PortForwardEditResult>
{
    private readonly PortForwardRule _original;
    private readonly bool _isNew;

    /// <param name="original">Rule to edit; null for a new one.</param>
    /// <param name="offerSaveToHost">Show "Save to host" (when adding from a running session).</param>
    public PortForwardEditorDialogViewModel(PortForwardRule? original, bool offerSaveToHost = false)
    {
        _isNew = original is null;
        _original = original ?? new PortForwardRule();
        OfferSaveToHost = offerSaveToHost;

        IsLocal = _original.Kind == ForwardKind.Local;
        IsRemote = _original.Kind == ForwardKind.Remote;
        IsDynamic = _original.Kind == ForwardKind.Dynamic;
        BindAddress = _original.BindAddress;
        BindPort = _original.BindPort > 0 ? _original.BindPort.ToString() : string.Empty;
        DestinationHost = _original.DestinationHost;
        DestinationPort = _original.DestinationPort > 0 ? _original.DestinationPort.ToString() : string.Empty;
        AutoStart = _original.AutoStart;
    }

    public override string Title => _isNew ? "New tunnel" : "Edit tunnel";

    public override double Width => 480;

    public bool OfferSaveToHost { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation), nameof(NeedsDestination), nameof(BindLabel), nameof(DestinationLabel))]
    public partial bool IsLocal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation), nameof(NeedsDestination), nameof(BindLabel), nameof(DestinationLabel))]
    public partial bool IsRemote { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation), nameof(NeedsDestination), nameof(BindLabel), nameof(DestinationLabel))]
    public partial bool IsDynamic { get; set; }

    [ObservableProperty]
    public partial string BindAddress { get; set; }

    [ObservableProperty]
    public partial string BindPort { get; set; }

    [ObservableProperty]
    public partial string DestinationHost { get; set; }

    [ObservableProperty]
    public partial string DestinationPort { get; set; }

    [ObservableProperty]
    public partial bool AutoStart { get; set; }

    [ObservableProperty]
    public partial bool SaveToHost { get; set; }

    public bool NeedsDestination => !IsDynamic;

    public string BindLabel => IsRemote ? "Listen on the server" : "Listen on this computer";

    public string DestinationLabel => IsRemote ? "Forward to (from this computer)" : "Forward to (from the server)";

    public string Explanation => Kind switch
    {
        ForwardKind.Remote => "Connections to a port on the server are forwarded to an address reachable from this computer (ssh -R).",
        ForwardKind.Dynamic => "A SOCKS5 proxy on this computer; traffic leaves through the server (ssh -D).",
        _ => "Connections to a port on this computer are forwarded to an address reachable from the server (ssh -L).",
    };

    private ForwardKind Kind => IsRemote ? ForwardKind.Remote : IsDynamic ? ForwardKind.Dynamic : ForwardKind.Local;

    [RelayCommand]
    private void Save()
    {
        int.TryParse(BindPort, out var bindPort);
        int.TryParse(DestinationPort, out var destinationPort);

        var rule = _original with
        {
            Kind = Kind,
            BindAddress = BindAddress.Trim(),
            BindPort = bindPort,
            DestinationHost = DestinationHost.Trim(),
            DestinationPort = destinationPort,
            AutoStart = AutoStart,
        };

        if (rule.Validate() is { } problem)
        {
            Error = problem;
            return;
        }

        Close(new PortForwardEditResult(rule, SaveToHost));
    }
}
