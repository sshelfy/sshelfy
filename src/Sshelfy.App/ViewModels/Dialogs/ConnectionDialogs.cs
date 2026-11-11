using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>Asks whether to trust an unknown or changed host key.</summary>
public sealed partial class HostKeyDialogViewModel : DialogViewModel<bool>
{
    public HostKeyDialogViewModel(HostKeyChallenge challenge)
    {
        Challenge = challenge;
    }

    public HostKeyChallenge Challenge { get; }

    /// <summary>Not a plain first connection: the saved key changed, or a key of another type was offered.</summary>
    public bool IsChanged => Challenge.Status is HostKeyStatus.Changed or HostKeyStatus.OtherTypeKnown;

    public override string Title => Challenge.Status switch
    {
        HostKeyStatus.Changed => "Host key changed",
        HostKeyStatus.OtherTypeKnown => "Different host key type",
        _ => "Unknown host",
    };

    public override double Width => 480;

    public string Endpoint => Challenge.Port == 22 ? Challenge.Host : $"{Challenge.Host}:{Challenge.Port}";

    public string Message => Challenge.Status switch
    {
        HostKeyStatus.Changed => $"The key presented by {Endpoint} does not match the one saved earlier. This can mean the server was reinstalled, or that someone is intercepting the connection. Only continue if you know why the key changed.",
        HostKeyStatus.OtherTypeKnown => $"{Endpoint} is known with a {Challenge.PreviousFingerprint?.Split(' ')[0]} key, but now offers only a {Challenge.Algorithm} key. Someone intercepting the connection could do this. Only continue if the server's keys were changed on purpose.",
        _ => $"This is the first connection to {Endpoint}. Verify the fingerprint with the server administrator before trusting it.",
    };

    public string ConfirmText => IsChanged ? "Trust new key" : "Trust and connect";

    [RelayCommand]
    private void Trust() => Close(true);
}

public sealed record CredentialPromptResult(string Password, bool Save);

/// <summary>Asks for a password that is not saved (or was rejected).</summary>
public sealed partial class CredentialPromptDialogViewModel : DialogViewModel<CredentialPromptResult>
{
    public CredentialPromptDialogViewModel(string endpoint, bool canSave, string? message, string? label = null, string confirmText = "Connect")
    {
        Endpoint = endpoint;
        CanSave = canSave;
        Message = message;
        Label = label ?? $"Password for {endpoint}";
        ConfirmText = confirmText;
    }

    public override string Title => ConfirmText == "Connect" ? "Password required" : "Passphrase required";

    public string Label { get; }

    public string ConfirmText { get; }

    public override double Width => 400;

    public string Endpoint { get; }

    public bool CanSave { get; }

    public string? Message { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Save { get; set; }

    private bool CanSubmit => Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit() => Close(new CredentialPromptResult(Password, Save && CanSave));
}

public sealed record QuickConnectRequest(HostProfile Host, string? Password, bool SaveHost);

/// <summary>Connect to user@host[:port] without creating a host first.</summary>
public sealed partial class QuickConnectDialogViewModel : DialogViewModel<QuickConnectRequest>
{
    public QuickConnectDialogViewModel(string? initial)
    {
        Input = initial ?? string.Empty;
    }

    public override string Title => "Quick connect";

    public override double Width => 440;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(IsValid))]
    public partial string Input { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SaveHost { get; set; }

    public bool IsValid => QuickConnectParser.TryParse(Input, out _, out _, out _);

    public string Hint => QuickConnectParser.TryParse(Input, out var user, out var address, out var port)
        ? $"User {user} · host {address} · port {port}"
        : "Format: user@host or user@host:port";

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Connect()
    {
        if (!QuickConnectParser.TryParse(Input, out var user, out var address, out var port))
            return;

        var host = new HostProfile { Name = address, Address = address, Port = port, Username = user };
        Close(new QuickConnectRequest(host, Password.Length > 0 ? Password : null, SaveHost));
    }
}
