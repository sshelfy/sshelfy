using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Security;

namespace Sshelfy.App.Services;

/// <summary>Makes sure the vault is usable before an operation that needs secrets.</summary>
public interface IVaultGate
{
    /// <returns>False if the user cancelled.</returns>
    Task<bool> EnsureUnlockedAsync(string? reason = null);
}

internal sealed class VaultGate : IVaultGate
{
    private readonly ISecretVault _vault;
    private readonly IDialogService _dialogs;

    public VaultGate(ISecretVault vault, IDialogService dialogs)
    {
        _vault = vault;
        _dialogs = dialogs;
    }

    public async Task<bool> EnsureUnlockedAsync(string? reason = null)
    {
        if (_vault.State == VaultState.Unlocked)
            return true;

        return await _dialogs.ShowAsync(new VaultDialogViewModel(_vault, reason));
    }
}
