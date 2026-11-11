using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Security;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>Creates the vault (first use) or unlocks it with the master password.</summary>
public sealed partial class VaultDialogViewModel : DialogViewModel<bool>
{
    private const int MinimumLength = 8;

    private readonly ISecretVault _vault;

    public VaultDialogViewModel(ISecretVault vault, string? reason = null)
    {
        _vault = vault;
        IsCreating = vault.State == VaultState.NotCreated;
        Reason = reason;
        RememberOnDevice = vault.IsRememberedOnDevice || IsCreating;

        // Offer Hello right away; the password field stays as the fallback.
        if (CanUseHello)
            Dispatcher.UIThread.Post(() => UseHelloCommand.Execute(null), DispatcherPriority.Background);
    }

    public bool CanUseHello => !IsCreating && _vault.IsHelloEnabled;

    [RelayCommand]
    private async Task UseHello()
    {
        if (IsBusy)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            switch (await _vault.UnlockWithHelloAsync())
            {
                case HelloStatus.Success:
                    Close(true);
                    break;
                case HelloStatus.Cancelled:
                    break;
                case HelloStatus.KeyMissing:
                    Error = "Windows Hello no longer matches this vault (for example after a PIN reset). Use the master password; you can turn Hello on again in Settings.";
                    OnPropertyChanged(nameof(CanUseHello));
                    break;
                case HelloStatus.Unavailable:
                    Error = "Windows Hello is not available right now. Use the master password.";
                    break;
                default:
                    Error = "Windows Hello did not work. Use the master password.";
                    break;
            }
        }
        finally
        {
            IsBusy = false;
            SubmitCommand.NotifyCanExecuteChanged();
        }
    }

    public override string Title => IsCreating ? "Set a master password" : "Unlock vault";

    public override double Width => 420;

    public bool IsCreating { get; }

    public string? Reason { get; }

    public string Description => IsCreating
        ? "Passwords and private keys are encrypted with this password. It cannot be recovered, so keep it somewhere safe."
        : "Enter your master password to access saved passwords and keys.";

    public bool CanRememberOnDevice => _vault.CanRememberOnDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Confirmation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RememberOnDevice { get; set; }

    private bool CanSubmit => !IsBusy && Password.Length > 0 && (!IsCreating || Confirmation.Length > 0);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        Error = null;
        if (IsCreating)
        {
            if (Password.Length < MinimumLength)
            {
                Error = $"Use at least {MinimumLength} characters.";
                return;
            }

            if (Password != Confirmation)
            {
                Error = "The passwords don't match.";
                return;
            }
        }

        IsBusy = true;
        SubmitCommand.NotifyCanExecuteChanged();
        try
        {
            var remember = RememberOnDevice && CanRememberOnDevice;
            if (IsCreating)
            {
                await _vault.CreateAsync(Password, remember);
            }
            else if (!await _vault.UnlockAsync(Password, remember))
            {
                Error = "Incorrect password.";
                Password = string.Empty;
                return;
            }

            Close(true);
        }
        finally
        {
            IsBusy = false;
            SubmitCommand.NotifyCanExecuteChanged();
        }
    }
}
