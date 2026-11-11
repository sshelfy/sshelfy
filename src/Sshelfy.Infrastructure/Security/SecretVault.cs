using System.Security.Cryptography;
using System.Text;
using Sshelfy.Core.Security;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Infrastructure.Security;

/// <summary>
/// Master-password vault. A 256-bit key is derived with PBKDF2-HMAC-SHA256 and every entry is
/// sealed with AES-256-GCM, using the entry name as associated data so ciphertexts cannot be
/// swapped between entries. Optionally the derived key is kept on disk protected by the OS
/// user account (DPAPI on Windows), which allows unlocking without typing the password.
/// </summary>
internal sealed class SecretVault : ISecretVault, IDisposable
{
    private const int Iterations = 600_000;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    // Frozen: "shellfish" is the app's former name. These strings are part of the vault format and its key derivation (here and in HelloWrapKey, DeriveKeyAsync); changing them makes existing vaults, Hello unlock, history and recordings unreadable.
    private const string VerifierPlaintext = "shellfish-vault-v1";
    private const string VerifierContext = "\0verifier";
    private const string HelloContext = "\0hello";

    private readonly JsonFileStore<VaultDocument> _store;
    private readonly IDeviceKeyProtector _deviceProtector;
    private readonly string _deviceKeyPath;
    private readonly IHelloSigner _hello;
    private readonly JsonFileStore<HelloDocument> _helloStore;
    private readonly string _helloPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VaultDocument? _document;
    private byte[]? _key;

    public SecretVault(StoragePaths paths, IDeviceKeyProtector deviceProtector, IHelloSigner? hello = null)
    {
        _store = new JsonFileStore<VaultDocument>(paths.File("vault.json"), () => null!);
        _deviceProtector = deviceProtector;
        _deviceKeyPath = paths.File("vault.device");
        _hello = hello ?? new NoHelloSigner();
        _helloPath = paths.File("vault.hello");
        _helloStore = new JsonFileStore<HelloDocument>(_helloPath, () => null!);
    }

    public bool IsHelloEnabled => _document is not null && File.Exists(_helloPath);

    public Task<bool> IsHelloAvailableAsync() => _hello.IsAvailableAsync();

    /// <summary>
    /// A fixed random challenge is signed by the Hello key (which needs the user's gesture). The
    /// signature is deterministic (RSA PKCS#1 v1.5), so HKDF turns it into the same wrapping key every
    /// time; that key seals the vault key in vault.hello. Without the gesture the key cannot be rebuilt,
    /// unlike DPAPI, which any program running as the user could call.
    /// </summary>
    public async Task<HelloStatus> EnableHelloAsync(CancellationToken cancellationToken = default)
    {
        var (document, key) = RequireUnlocked();
        if (!await _hello.IsAvailableAsync().ConfigureAwait(false))
            return HelloStatus.Unavailable;

        var created = await _hello.CreateKeyAsync().ConfigureAwait(false);
        if (created != HelloStatus.Success)
            return created;

        var challenge = RandomNumberGenerator.GetBytes(32);
        var signature = await _hello.SignAsync(challenge).ConfigureAwait(false);
        if (signature.Status != HelloStatus.Success || signature.Signature is null)
            return signature.Status == HelloStatus.Success ? HelloStatus.Failed : signature.Status;

        var wrap = HelloWrapKey(signature.Signature, document.Salt);
        var helloDocument = new HelloDocument
        {
            Challenge = Convert.ToBase64String(challenge),
            Key = Seal(wrap, HelloContext, Convert.ToBase64String(key)),
        };
        CryptographicOperations.ZeroMemory(wrap);

        await _helloStore.WriteAsync(helloDocument, cancellationToken).ConfigureAwait(false);
        ForgetDevice(); // Hello and automatic unlock exclude each other.
        return HelloStatus.Success;
    }

    public async Task<HelloStatus> UnlockWithHelloAsync(CancellationToken cancellationToken = default)
    {
        if (_document is not { } document || !IsHelloEnabled)
            return HelloStatus.Unavailable;
        if (await _helloStore.ReadAsync(cancellationToken).ConfigureAwait(false) is not { } helloDocument)
            return HelloStatus.Unavailable;

        var signature = await _hello.SignAsync(Convert.FromBase64String(helloDocument.Challenge)).ConfigureAwait(false);
        if (signature.Status is HelloStatus.Cancelled or HelloStatus.Unavailable or HelloStatus.Failed)
            return signature.Status;
        if (signature.Status != HelloStatus.Success || signature.Signature is null)
        {
            await DisableHelloAsync().ConfigureAwait(false);
            return HelloStatus.KeyMissing;
        }

        var wrap = HelloWrapKey(signature.Signature, document.Salt);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(Open(wrap, HelloContext, helloDocument.Key));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A new Hello key (reset, new PIN, other device) signs differently: fall back to the password.
            await DisableHelloAsync().ConfigureAwait(false);
            return HelloStatus.KeyMissing;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrap);
        }

        if (!Verify(key, document))
        {
            CryptographicOperations.ZeroMemory(key);
            await DisableHelloAsync().ConfigureAwait(false);
            return HelloStatus.KeyMissing;
        }

        SetKey(key);
        return HelloStatus.Success;
    }

    public async Task DisableHelloAsync()
    {
        try
        {
            File.Delete(_helloPath);
        }
        catch (IOException)
        {
        }

        await _hello.DeleteKeyAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static byte[] HelloWrapKey(byte[] signature, string salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, KeySize, Convert.FromBase64String(salt), Encoding.UTF8.GetBytes("shellfish/hello"));

    public VaultState State => _document is null ? VaultState.NotCreated : _key is null ? VaultState.Locked : VaultState.Unlocked;

    public bool CanRememberOnDevice => _deviceProtector.IsSupported;

    public bool IsRememberedOnDevice => CanRememberOnDevice && File.Exists(_deviceKeyPath);

    public event EventHandler? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _document = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (_document is null || !IsRememberedOnDevice)
            return;

        try
        {
            var protectedKey = await File.ReadAllBytesAsync(_deviceKeyPath, cancellationToken).ConfigureAwait(false);
            var key = _deviceProtector.Unprotect(protectedKey, Convert.FromBase64String(_document.Salt));
            if (Verify(key, _document))
                SetKey(key);
            else
                ForgetDevice();
        }
        catch (CryptographicException)
        {
            // Different user/machine or corrupted file: fall back to the master password.
            ForgetDevice();
        }
    }

    public async Task CreateAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        if (_document is not null)
            throw new InvalidOperationException("The vault already exists.");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = await Task.Run(() => DeriveKey(masterPassword, salt, Iterations), cancellationToken).ConfigureAwait(false);

        var document = new VaultDocument
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Verifier = Seal(key, VerifierContext, VerifierPlaintext),
        };

        await _store.WriteAsync(document, cancellationToken).ConfigureAwait(false);
        _document = document;
        await RememberAsync(key, salt, rememberOnDevice, cancellationToken).ConfigureAwait(false);
        SetKey(key);
    }

    public async Task<bool> UnlockAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default)
    {
        var document = _document ?? throw new InvalidOperationException("The vault has not been created.");
        var salt = Convert.FromBase64String(document.Salt);
        var key = await Task.Run(() => DeriveKey(masterPassword, salt, document.Iterations), cancellationToken).ConfigureAwait(false);

        if (!Verify(key, document))
        {
            CryptographicOperations.ZeroMemory(key);
            return false;
        }

        await RememberAsync(key, salt, rememberOnDevice, cancellationToken).ConfigureAwait(false);
        SetKey(key);
        return true;
    }

    public void Lock()
    {
        if (_key is null)
            return;

        CryptographicOperations.ZeroMemory(_key);
        _key = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ForgetDevice()
    {
        try
        {
            File.Delete(_deviceKeyPath);
        }
        catch (IOException)
        {
            // Best effort; the file will be ignored if it cannot be decrypted anyway.
        }
    }

    public async Task<bool> RememberOnDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (_document is null || _key is null || !CanRememberOnDevice)
            return false;

        await RememberAsync(_key, Convert.FromBase64String(_document.Salt), remember: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default)
    {
        var (_, vaultKey) = RequireUnlocked();
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, KeySize, salt: null, info: Encoding.UTF8.GetBytes("shellfish/" + purpose));
        return Task.FromResult(key);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, vaultKey) = RequireUnlocked();
            return document.Entries.TryGetValue(key, out var sealedValue) ? Open(vaultKey, key, sealedValue) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, vaultKey) = RequireUnlocked();
            if (value is null)
            {
                if (!document.Entries.Remove(key))
                    return;
            }
            else
            {
                document.Entries[key] = Seal(vaultKey, key, value);
            }

            await _store.WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, _) = RequireUnlocked();
            var keys = document.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (keys.Count == 0)
                return;

            foreach (var k in keys)
                document.Entries.Remove(k);

            await _store.WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
        _gate.Dispose();
    }

    internal static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static string Seal(byte[] key, string context, string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var buffer = new byte[NonceSize + TagSize + plainBytes.Length];
        var nonce = buffer.AsSpan(0, NonceSize);
        var tag = buffer.AsSpan(NonceSize, TagSize);
        var cipher = buffer.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag, Encoding.UTF8.GetBytes(context));
        }

        CryptographicOperations.ZeroMemory(plainBytes);
        return Convert.ToBase64String(buffer);
    }

    internal static string Open(byte[] key, string context, string sealedValue)
    {
        var buffer = Convert.FromBase64String(sealedValue);
        if (buffer.Length < NonceSize + TagSize)
            throw new CryptographicException("Vault entry is truncated.");

        var plain = new byte[buffer.Length - NonceSize - TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(
                buffer.AsSpan(0, NonceSize),
                buffer.AsSpan(NonceSize + TagSize),
                buffer.AsSpan(NonceSize, TagSize),
                plain,
                Encoding.UTF8.GetBytes(context));
        }

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static bool Verify(byte[] key, VaultDocument document)
    {
        try
        {
            return Open(key, VerifierContext, document.Verifier) == VerifierPlaintext;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task RememberAsync(byte[] key, byte[] salt, bool remember, CancellationToken cancellationToken)
    {
        if (remember && CanRememberOnDevice)
        {
            await File.WriteAllBytesAsync(_deviceKeyPath, _deviceProtector.Protect(key, salt), cancellationToken).ConfigureAwait(false);
            if (IsHelloEnabled)
                await DisableHelloAsync().ConfigureAwait(false);
        }
        else
        {
            ForgetDevice();
        }
    }

    private void SetKey(byte[] key)
    {
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
        _key = key;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private (VaultDocument Document, byte[] Key) RequireUnlocked() =>
        _document is not null && _key is not null ? (_document, _key) : throw new VaultLockedException();

    internal sealed class HelloDocument
    {
        public int Version { get; set; } = 1;

        /// <summary>Random data the Hello key signs; the signature yields the wrapping key.</summary>
        public string Challenge { get; set; } = string.Empty;

        /// <summary>The vault key sealed with the wrapping key.</summary>
        public string Key { get; set; } = string.Empty;
    }

    internal sealed class VaultDocument
    {
        public int Version { get; set; } = 1;

        public string Kdf { get; set; } = "PBKDF2-HMAC-SHA256";

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public string Verifier { get; set; } = string.Empty;

        public Dictionary<string, string> Entries { get; set; } = new(StringComparer.Ordinal);
    }
}
