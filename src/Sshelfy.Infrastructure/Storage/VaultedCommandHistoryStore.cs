using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sshelfy.Core.Abstractions;
using Sshelfy.Core.History;
using Sshelfy.Core.Security;

namespace Sshelfy.Infrastructure.Storage;

/// <summary>
/// Command history in <c>history.enc</c>, sealed with AES-256-GCM under a key derived from the vault,
/// once a master password exists. Without a vault it stays in plain <c>history.json</c>; the first
/// save with an unlocked vault moves it over and deletes the plain file. While the vault is locked
/// the store is unavailable (reads return nothing, writes are skipped) and the history service merges
/// what it recorded in the meantime when the vault opens.
/// </summary>
internal sealed class VaultedCommandHistoryStore : ICommandHistoryStore
{
    private const string Purpose = "command-history";
    private static readonly byte[] Magic = "SFHI1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ISecretVault _vault;
    private readonly ILogger? _logger;
    private readonly JsonFileStore<CommandHistoryData> _plain;
    private readonly string _plainPath;
    private readonly string _encryptedPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VaultedCommandHistoryStore(StoragePaths paths, ISecretVault vault, ILogger<VaultedCommandHistoryStore>? logger = null)
    {
        _vault = vault;
        _logger = logger;
        _plainPath = paths.File("history.json");
        _encryptedPath = paths.File("history.enc");
        _plain = new JsonFileStore<CommandHistoryData>(_plainPath, () => new CommandHistoryData());
        _vault.StateChanged += (_, _) => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAvailable => _vault.State != VaultState.Locked;

    public event EventHandler? AvailabilityChanged;

    public async Task<CommandHistoryData> LoadAsync(CancellationToken cancellationToken = default)
    {
        switch (_vault.State)
        {
            case VaultState.NotCreated:
                return await _plain.ReadAsync(cancellationToken).ConfigureAwait(false);
            case VaultState.Locked:
                return new CommandHistoryData();
        }

        var data = await ReadEncryptedAsync(cancellationToken).ConfigureAwait(false) ?? new CommandHistoryData();

        // Not migrated yet: the plain file still holds history from before the vault existed.
        if (File.Exists(_plainPath))
            data = CommandHistoryMerge.Merge(data, await _plain.ReadAsync(cancellationToken).ConfigureAwait(false));
        return data;
    }

    public async Task SaveAsync(CommandHistoryData data, CancellationToken cancellationToken = default)
    {
        switch (_vault.State)
        {
            case VaultState.NotCreated:
                await _plain.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                return;
            case VaultState.Locked:
                return; // saved again when the vault opens
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonFileStore<CommandHistoryData>.Options);
        byte[] key;
        try
        {
            key = await _vault.DeriveKeyAsync(Purpose, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            return; // locked between the check and the save
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[json.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, json, cipher, tag, Magic);
        CryptographicOperations.ZeroMemory(key);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _encryptedPath + ".tmp";
            await using (var file = File.Create(temp))
            {
                await file.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, _encryptedPath, overwrite: true);
            if (File.Exists(_plainPath))
            {
                File.Delete(_plainPath);
                _logger?.LogInformation("Command history moved into the encrypted store");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CommandHistoryData?> ReadEncryptedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_encryptedPath))
            return null;

        byte[] bytes;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bytes = await File.ReadAllBytesAsync(_encryptedPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var header = Magic.Length + NonceSize + TagSize;
        if (bytes.Length < header || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            _logger?.LogWarning("history.enc is not a sshelfy history file; ignoring it");
            return null;
        }

        var key = await _vault.DeriveKeyAsync(Purpose, cancellationToken).ConfigureAwait(false);
        try
        {
            var plain = new byte[bytes.Length - header];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(
                    bytes.AsSpan(Magic.Length, NonceSize),
                    bytes.AsSpan(header),
                    bytes.AsSpan(Magic.Length + NonceSize, TagSize),
                    plain,
                    Magic);
            }

            return JsonSerializer.Deserialize<CommandHistoryData>(plain, JsonFileStore<CommandHistoryData>.Options);
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or CryptographicException or JsonException)
        {
            // A different vault (e.g. recreated): keep the file aside rather than overwriting it.
            File.Copy(_encryptedPath, _encryptedPath + ".unreadable", overwrite: true);
            _logger?.LogWarning("history.enc could not be decrypted with the current vault ({Error})", ex.GetType().Name);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
