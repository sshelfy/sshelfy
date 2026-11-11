using Avalonia.Threading;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.Services;

/// <summary>Shows the host key dialog on the UI thread; called from the SSH handshake thread.</summary>
internal sealed class HostKeyPrompt : IHostKeyPrompt
{
    private readonly IDialogService _dialogs;

    public HostKeyPrompt(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public Task<bool> ConfirmAsync(HostKeyChallenge challenge) =>
        Dispatcher.UIThread.InvokeAsync(() => _dialogs.ShowAsync(new HostKeyDialogViewModel(challenge)));
}

/// <summary>Turns a host profile into connection parameters, unlocking the vault or asking for a password as needed.</summary>
public interface IConnectionResolver
{
    /// <param name="host">Host to connect to (may be an unsaved quick-connect host).</param>
    /// <param name="knownPassword">Password already supplied by the user, if any.</param>
    /// <param name="forcePrompt">Ask for the password even if one is saved (after an authentication failure).</param>
    /// <returns>Null if the user cancelled.</returns>
    /// <param name="cancellationToken">Stops a running pre-connect command (e.g. the tab was closed).</param>
    Task<ConnectionTarget?> ResolveAsync(HostProfile host, string? knownPassword = null, bool forcePrompt = false, CancellationToken cancellationToken = default);
}

internal sealed class ConnectionResolver : IConnectionResolver
{
    private readonly IHostCatalog _catalog;
    private readonly ISecretVault _vault;
    private readonly IVaultGate _vaultGate;
    private readonly IDialogService _dialogs;
    private readonly PreConnectGate? _preConnect;

    public ConnectionResolver(IHostCatalog catalog, ISecretVault vault, IVaultGate vaultGate, IDialogService dialogs, PreConnectGate? preConnect = null)
    {
        _preConnect = preConnect;
        _catalog = catalog;
        _vault = vault;
        _vaultGate = vaultGate;
        _dialogs = dialogs;
    }

    public async Task<ConnectionTarget?> ResolveAsync(HostProfile host, string? knownPassword = null, bool forcePrompt = false, CancellationToken cancellationToken = default)
    {
        var target = await ResolveHopAsync(host, knownPassword, forcePrompt);
        if (target is null)
            return null;

        // Jump hosts: each hop resolves its own credentials (prompting if needed).
        var visited = new HashSet<Guid> { host.Id };
        var current = host;
        var hops = new List<ConnectionTarget>();
        var chain = new List<HostProfile> { host };
        while (current.JumpHostId is { } jumpId)
        {
            if (!visited.Add(jumpId) || _catalog.FindHost(jumpId) is not { } jump)
                throw new SshConnectionFailedException($"The jump host configured for {current.DisplayName} is missing or loops back.");

            var hop = await ResolveHopAsync(jump, null, false);
            if (hop is null)
                return null;
            hops.Add(hop);
            chain.Add(jump);
            current = jump;
        }

        // Prepare from the outside in; only the host this PC reaches directly can be knocked on.
        if (_preConnect is not null)
        {
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (!await _preConnect.RunAsync(chain[i], knock: i == chain.Count - 1, cancellationToken))
                    return null;
            }
        }

        // Nest from the outermost hop inwards: target.Via = first jump, its Via = the next one…
        ConnectionTarget? via = null;
        for (var i = hops.Count - 1; i >= 0; i--)
            via = hops[i] with { Via = via };
        return target with { Via = via };
    }

    private async Task<ConnectionTarget?> ResolveHopAsync(HostProfile host, string? knownPassword, bool forcePrompt)
    {
        var isSaved = _catalog.FindHost(host.Id) is not null;

        // The agent holds the keys; nothing to unlock or ask for.
        if (host.AuthMethod == AuthMethod.Agent)
            return await _catalog.BuildTargetAsync(host);

        if (host.AuthMethod == AuthMethod.PrivateKey)
        {
            if (!await _vaultGate.EnsureUnlockedAsync($"Unlock the vault to use the key for {host.DisplayName}."))
                return null;
            return await _catalog.BuildTargetAsync(host);
        }

        var password = forcePrompt ? null : knownPassword;
        if (password is null && !forcePrompt && isSaved && _vault.State != VaultState.NotCreated)
        {
            if (_vault.State == VaultState.Locked)
                await _vaultGate.EnsureUnlockedAsync($"Unlock the vault to use the saved password for {host.DisplayName}.");
            if (_vault.State == VaultState.Unlocked)
                password = await _vault.GetAsync(SecretKeys.HostPassword(host.Id));
        }

        if (password is null)
        {
            var message = forcePrompt ? "The server rejected the password. Try again." : null;
            var result = await _dialogs.ShowAsync(new CredentialPromptDialogViewModel(host.Endpoint, isSaved, message));
            if (result is null)
                return null;

            password = result.Password;
            if (result.Save && await _vaultGate.EnsureUnlockedAsync("Passwords are stored in the encrypted vault."))
                await _catalog.SaveHostAsync(host, new SecretUpdate(password));
        }

        return await _catalog.BuildTargetAsync(host, password);
    }
}
