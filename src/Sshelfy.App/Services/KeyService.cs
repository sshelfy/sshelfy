using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.Services;

/// <summary>Creating SSH keys and putting their public half on servers.</summary>
public interface IKeyService
{
    /// <summary>Shows the generator; the new key is saved in the vault. Null when cancelled.</summary>
    Task<SshKeyProfile?> GenerateAsync();

    /// <summary>The authorized_keys line of a saved key (the vault must be unlocked).</summary>
    Task<string?> PublicKeyAsync(SshKeyProfile key);

    Task CopyPublicKeyAsync(SshKeyProfile key);

    /// <summary>Lets the user pick (or generate) a key and adds it to the host's authorized_keys.</summary>
    Task InstallOnHostAsync(HostProfile host);

    /// <summary>Logs in to <paramref name="host"/> the usual way and appends the key to ~/.ssh/authorized_keys.</summary>
    /// <returns>Null on success, otherwise a message for the user.</returns>
    Task<string?> InstallAsync(HostProfile host, SshKeyProfile key);
}

internal sealed class KeyService : IKeyService
{
    private readonly ISshKeyGenerator _generator;
    private readonly ISshKeyInspector _inspector;
    private readonly IHostCatalog _catalog;
    private readonly ISecretVault _vault;
    private readonly IVaultGate _vaultGate;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IConnectionResolver _resolver;
    private readonly ISshConnectionFactory _connections;
    private readonly ViewModels.StatusBarViewModel _status;

    public KeyService(
        ISshKeyGenerator generator,
        ISshKeyInspector inspector,
        IHostCatalog catalog,
        ISecretVault vault,
        IVaultGate vaultGate,
        IDialogService dialogs,
        IClipboardService clipboard,
        IConnectionResolver resolver,
        ISshConnectionFactory connections,
        ViewModels.StatusBarViewModel status)
    {
        _generator = generator;
        _inspector = inspector;
        _catalog = catalog;
        _vault = vault;
        _vaultGate = vaultGate;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _resolver = resolver;
        _connections = connections;
        _status = status;
    }

    public async Task<SshKeyProfile?> GenerateAsync()
    {
        if (!await _vaultGate.EnsureUnlockedAsync("New keys are stored in the encrypted vault."))
            return null;
        return await _dialogs.ShowAsync(new GenerateKeyDialogViewModel(_generator, _catalog, _clipboard));
    }

    public async Task<string?> PublicKeyAsync(SshKeyProfile key)
    {
        if (!await _vaultGate.EnsureUnlockedAsync("The key is stored in the encrypted vault."))
            return null;
        var material = await _vault.GetAsync(SecretKeys.KeyMaterial(key.Id));
        return material is null ? null : _inspector.PublicKey(material, await _vault.GetAsync(SecretKeys.KeyPassphrase(key.Id)), key.Name);
    }

    public async Task CopyPublicKeyAsync(SshKeyProfile key)
    {
        if (await PublicKeyAsync(key) is { } line)
        {
            await _clipboard.SetTextAsync(line);
            _status.Set($"Copied the public key of {key.Name}", ViewModels.StatusLevel.Ok);
        }
    }

    public async Task InstallOnHostAsync(HostProfile host)
    {
        if (await _dialogs.ShowAsync(new InstallKeyDialogViewModel(host, _catalog, this)) is { } installed)
            _status.Set($"Installed {installed.Name} on {host.DisplayName}", ViewModels.StatusLevel.Ok);
    }

    public async Task<string?> InstallAsync(HostProfile host, SshKeyProfile key)
    {
        string line;
        try
        {
            line = await PublicKeyAsync(key) ?? throw new InvalidOperationException("The private key could not be read from the vault.");
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        try
        {
            var target = await _resolver.ResolveAsync(host);
            if (target is null)
                return "Cancelled.";

            await using var connection = await _connections.ConnectAsync(target);
            var result = await connection.RunCommandAsync(AuthorizedKeys.InstallCommand(line));
            return result.ExitCode == 0
                ? null
                : $"The server refused: {(result.Error.Trim() is { Length: > 0 } error ? error : "exit code " + result.ExitCode)}";
        }
        catch (SshConnectionFailedException ex)
        {
            return ex.Message;
        }
    }
}
