namespace Sshelfy.Core.Security;

public enum VaultState
{
    /// <summary>No master password has been set yet.</summary>
    NotCreated,

    Locked,

    Unlocked,
}

/// <summary>
/// Encrypted store for passwords and private keys, protected by a master password.
/// Values can only be read or written while the vault is unlocked.
/// </summary>
public interface ISecretVault
{
    VaultState State { get; }

    /// <summary>True when the platform can keep the vault key protected by the OS user account.</summary>
    bool CanRememberOnDevice { get; }

    bool IsRememberedOnDevice { get; }

    event EventHandler? StateChanged;

    /// <summary>Loads the vault file and unlocks it automatically if a device key was remembered.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task CreateAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default);

    /// <returns>False if the password is wrong.</returns>
    Task<bool> UnlockAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default);

    /// <summary>Drops the key from memory. A remembered device key is kept.</summary>
    void Lock();

    /// <summary>Removes the remembered device key; the next start asks for the master password.</summary>
    void ForgetDevice();

    /// <summary>Stores the key of the unlocked vault for automatic unlock on this device.</summary>
    /// <returns>False if the vault is locked or the platform cannot protect the key.</returns>
    Task<bool> RememberOnDeviceAsync(CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores a value; <c>null</c> removes the entry.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);

    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>The vault key is wrapped by a Windows Hello key, so a Hello gesture can unlock it.</summary>
    bool IsHelloEnabled => false;

    /// <summary>Windows Hello is set up on this system.</summary>
    Task<bool> IsHelloAvailableAsync() => Task.FromResult(false);

    /// <summary>Lets Windows Hello unlock the vault from now on (the vault must be unlocked; asks for a Hello gesture).
    /// Replaces automatic unlock on this device, which would make Hello pointless.</summary>
    Task<HelloStatus> EnableHelloAsync(CancellationToken cancellationToken = default) => Task.FromResult(HelloStatus.Unavailable);

    /// <summary>Unlocks with a Hello gesture. If the Hello key no longer matches, Hello is turned off and <see cref="HelloStatus.KeyMissing"/> returned.</summary>
    Task<HelloStatus> UnlockWithHelloAsync(CancellationToken cancellationToken = default) => Task.FromResult(HelloStatus.Unavailable);

    Task DisableHelloAsync() => Task.CompletedTask;

    /// <summary>A 256-bit key for one purpose (e.g. encrypting session logs), derived from the vault key.</summary>
    /// <exception cref="VaultLockedException">The vault is locked.</exception>
    Task<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default);
}

public sealed class VaultLockedException() : InvalidOperationException("The vault is locked.");

/// <summary>Naming scheme for vault entries.</summary>
public static class SecretKeys
{
    public static string HostPassword(Guid hostId) => $"host/{hostId:N}/password";

    public static string HostPrefix(Guid hostId) => $"host/{hostId:N}/";

    public static string KeyMaterial(Guid keyId) => $"key/{keyId:N}/private";

    public static string KeyPassphrase(Guid keyId) => $"key/{keyId:N}/passphrase";

    public static string KeyPrefix(Guid keyId) => $"key/{keyId:N}/";
}

public sealed record SshKeyInfo(string Algorithm, string Fingerprint);

public enum SshKeyProblem
{
    None,
    InvalidFormat,
    PassphraseRequired,
    WrongPassphrase,
}

/// <summary>Validates private key material and extracts its public metadata.</summary>
public interface ISshKeyInspector
{
    /// <returns>Key metadata, or null with <paramref name="problem"/> describing why it failed.</returns>
    SshKeyInfo? Inspect(string privateKey, string? passphrase, out SshKeyProblem problem);

    /// <summary>The authorized_keys line ("type base64 comment") for a private key, or null if it cannot be read.</summary>
    string? PublicKey(string privateKey, string? passphrase, string comment) => null;
}
