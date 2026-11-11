using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sshelfy.Core.Backup;
using Sshelfy.Infrastructure.Security;

namespace Sshelfy.Infrastructure.Storage;

/// <summary>
/// A backup is a small JSON envelope: KDF parameters plus the content encrypted with AES-256-GCM
/// under a key derived from the export password (PBKDF2-HMAC-SHA256, 600,000 iterations). The
/// password is independent of the master password, so a backup can be restored on another machine.
/// </summary>
internal sealed class EncryptedBackupFile : IBackupFile
{
    // Frozen: "shellfish" is the app's former name. These strings are part of the backup file format; changing them makes existing backups unreadable.
    private const string Format = "shellfish-backup";
    private const int CurrentVersion = 1;
    private const int Iterations = 600_000;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("shellfish-backup-v1");

    public async Task WriteAsync(string path, BackupContent content, string password, CancellationToken cancellationToken = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(content, JsonFileStore<BackupContent>.Options);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = await Task.Run(() => SecretVault.DeriveKey(password, salt, Iterations), cancellationToken).ConfigureAwait(false);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }

        var envelope = new Envelope
        {
            Format = Format,
            Version = CurrentVersion,
            Kdf = "PBKDF2-HMAC-SHA256",
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher),
        };

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonFileStore<Envelope>.Options), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<BackupContent> ReadAsync(string path, string password, CancellationToken cancellationToken = default)
    {
        Envelope? envelope;
        try
        {
            await using var stream = File.OpenRead(path);
            envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, JsonFileStore<Envelope>.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null || envelope.Format != Format)
            throw new BackupFormatException("This is not a sshelfy backup file.");
        if (envelope.Version > CurrentVersion)
            throw new BackupFormatException("This backup was made by a newer version of sshelfy.");

        // A crafted file must not keep the CPU busy for hours (or crash the derivation with a negative count).
        if (envelope.Iterations is < 100_000 or > 10_000_000)
            throw new BackupFormatException("The backup file is damaged.");

        byte[] salt, nonce, tag, cipher;
        try
        {
            (salt, nonce, tag, cipher) = (Convert.FromBase64String(envelope.Salt), Convert.FromBase64String(envelope.Nonce),
                Convert.FromBase64String(envelope.Tag), Convert.FromBase64String(envelope.Data));
        }
        catch (FormatException)
        {
            throw new BackupFormatException("The backup file is damaged.");
        }

        var key = await Task.Run(() => SecretVault.DeriveKey(password, salt, envelope.Iterations), cancellationToken).ConfigureAwait(false);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
            return JsonSerializer.Deserialize<BackupContent>(plain, JsonFileStore<BackupContent>.Options)
                   ?? throw new BackupFormatException("The backup file is empty.");
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or CryptographicException)
        {
            throw new BackupPasswordException();
        }
        catch (JsonException)
        {
            throw new BackupFormatException("The backup content could not be read.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal sealed class Envelope
    {
        public string Format { get; set; } = string.Empty;

        public int Version { get; set; }

        public string Kdf { get; set; } = string.Empty;

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string Data { get; set; } = string.Empty;
    }
}
