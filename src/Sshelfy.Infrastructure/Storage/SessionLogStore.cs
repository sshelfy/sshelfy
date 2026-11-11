using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Terminal;

namespace Sshelfy.Infrastructure.Storage;

/// <summary>
/// Session recordings on disk: "{host}_{yyyy-MM-dd_HH-mm-ss}.log|.cast", with ".enc" appended when
/// encrypted. Encrypted files: "SFLG" + version + 16-byte salt, then chunks of
/// [uint32 length][12-byte nonce][16-byte tag][ciphertext] sealed with AES-256-GCM. The file key
/// is derived (HKDF) from a vault subkey and the file's salt; each chunk's index is bound as
/// associated data so chunks cannot be reordered or dropped from the middle unnoticed.
/// </summary>
internal sealed partial class SessionLogStore : ISessionLogStore
{
    internal static readonly byte[] Magic = "SFLG"u8.ToArray();
    private const byte Version = 1;
    private const string VaultPurpose = "session-logs";

    private readonly StoragePaths _paths;
    private readonly ISettingsService _settings;
    private readonly ISecretVault _vault;

    public SessionLogStore(StoragePaths paths, ISettingsService settings, ISecretVault vault)
    {
        _paths = paths;
        _settings = settings;
        _vault = vault;
    }

    public string Directory => _settings.Current.SessionLogDirectory is { Length: > 0 } custom
        ? custom
        : Path.Combine(_paths.Root, "logs", "sessions");

    public async Task<ISessionLogSink> CreateAsync(string hostName, SessionLogFormat format, bool encrypt, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var extension = format == SessionLogFormat.Asciicast ? ".cast" : ".log";
        var name = $"{SafeName(hostName)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{extension}{(encrypt ? ".enc" : string.Empty)}";
        var path = Unique(Path.Combine(Directory, name));

        byte[]? key = null;
        byte[]? salt = null;
        if (encrypt)
        {
            salt = RandomNumberGenerator.GetBytes(16);
            key = FileKey(await _vault.DeriveKeyAsync(VaultPurpose, cancellationToken).ConfigureAwait(false), salt);
        }

        return new FileSessionLogSink(path, key, salt);
    }

    public IReadOnlyList<SessionLogInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        var result = new List<SessionLogInfo>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            var match = FileNamePattern().Match(Path.GetFileName(file));
            if (!match.Success)
                continue;

            var started = DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
                ? new DateTimeOffset(t)
                : new DateTimeOffset(File.GetCreationTime(file));
            result.Add(new SessionLogInfo(
                file,
                match.Groups["host"].Value,
                started,
                new FileInfo(file).Length,
                match.Groups["ext"].Value == "cast" ? SessionLogFormat.Asciicast : SessionLogFormat.PlainText,
                match.Groups["enc"].Success));
        }

        return result.OrderByDescending(l => l.Started).ToList();
    }

    public async Task<string> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith(Magic))
            return Encoding.UTF8.GetString(bytes);

        if (bytes.Length < 21 || bytes[4] != Version)
            throw new InvalidDataException("Unsupported encrypted log format.");

        var salt = bytes[5..21];
        var key = FileKey(await _vault.DeriveKeyAsync(VaultPurpose, cancellationToken).ConfigureAwait(false), salt);
        return Decrypt(bytes.AsSpan(21), key);
    }

    public int DeleteOlderThan(int retentionDays)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        var deleted = 0;
        foreach (var log in List().Where(l => l.Started < cutoff))
        {
            try
            {
                File.Delete(log.FilePath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    public void Delete(string filePath)
    {
        if (Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase))
            File.Delete(filePath);
    }

    // Frozen: "shellfish" is the app's former name. These strings are part of the recording key derivation; changing them makes existing encrypted recordings unreadable.
    internal static byte[] FileKey(byte[] vaultSubkey, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultSubkey, 32, salt, "shellfish/session-log-file"u8.ToArray());

    internal static byte[] ChunkAad(long index)
    {
        var aad = new byte[12];
        Magic.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(4), index);
        return aad;
    }

    internal static string Decrypt(ReadOnlySpan<byte> chunks, byte[] key)
    {
        var text = new StringBuilder();
        using var aes = new AesGcm(key, 16);
        long index = 0;
        while (chunks.Length > 0)
        {
            if (chunks.Length < 32)
                throw new InvalidDataException("Encrypted log is truncated.");
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(chunks);
            if (length < 0 || chunks.Length < 32 + length)
                throw new InvalidDataException("Encrypted log is truncated.");

            var plain = new byte[length];
            aes.Decrypt(chunks.Slice(4, 12), chunks.Slice(32, length), chunks.Slice(16, 16), plain, ChunkAad(index++));
            text.Append(Encoding.UTF8.GetString(plain));
            chunks = chunks[(32 + length)..];
        }

        return text.ToString();
    }

    private static string SafeName(string hostName)
    {
        var safe = InvalidChars().Replace(hostName, "-").Trim('-', '.');
        return safe.Length == 0 ? "session" : safe[..Math.Min(safe.Length, 60)];
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;
        var directory = Path.GetDirectoryName(path)!;
        var file = Path.GetFileName(path);
        var dot = file.IndexOf('.');
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{file[..dot]}-{i}{file[dot..]}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    [GeneratedRegex(@"^(?<host>.+)_(?<time>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:-\d+)?\.(?<ext>log|cast)(?<enc>\.enc)?$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"[^A-Za-z0-9._@-]+")]
    private static partial Regex InvalidChars();

    /// <summary>Buffers text on a background thread and writes it in chunks (encrypted or not).</summary>
    private sealed class FileSessionLogSink : ISessionLogSink
    {
        private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly FileStream _stream;
        private readonly byte[]? _key;
        private readonly Task _writer;
        private long _length;
        private long _chunkIndex;

        public FileSessionLogSink(string path, byte[]? key, byte[]? salt)
        {
            FilePath = path;
            _key = key;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (key is not null)
            {
                _stream.Write(Magic);
                _stream.WriteByte(Version);
                _stream.Write(salt);
                _stream.Flush();
            }

            _writer = Task.Run(WriteLoopAsync);
        }

        public string FilePath { get; }

        public long Length => Interlocked.Read(ref _length);

        public void Write(string text)
        {
            if (text.Length == 0)
                return;
            Interlocked.Add(ref _length, Encoding.UTF8.GetByteCount(text));
            _queue.Writer.TryWrite(text);
        }

        public async ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            if (_key is not null)
                CryptographicOperations.ZeroMemory(_key);
        }

        private async Task WriteLoopAsync()
        {
            var batch = new StringBuilder();
            try
            {
                while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    // Collect for up to a second (or 64 KB) to keep chunks and syscalls few.
                    var deadline = DateTime.UtcNow.AddSeconds(1);
                    while (batch.Length < 64 * 1024)
                    {
                        while (batch.Length < 64 * 1024 && _queue.Reader.TryRead(out var text))
                            batch.Append(text);
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero || _queue.Reader.Completion.IsCompleted)
                            break;
                        using var timeout = new CancellationTokenSource(remaining);
                        try
                        {
                            if (!await _queue.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                                break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    if (batch.Length > 0)
                    {
                        await WriteChunkAsync(Encoding.UTF8.GetBytes(batch.ToString())).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
            }
            catch (IOException)
            {
                // Disk full or removed: stop recording silently rather than break the session.
            }
        }

        private async Task WriteChunkAsync(byte[] plain)
        {
            if (_key is null)
            {
                await _stream.WriteAsync(plain).ConfigureAwait(false);
            }
            else
            {
                var chunk = new byte[32 + plain.Length];
                BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)plain.Length);
                RandomNumberGenerator.Fill(chunk.AsSpan(4, 12));
                using (var aes = new AesGcm(_key, 16))
                    aes.Encrypt(chunk.AsSpan(4, 12), plain, chunk.AsSpan(32), chunk.AsSpan(16, 16), ChunkAad(_chunkIndex++));
                await _stream.WriteAsync(chunk).ConfigureAwait(false);
            }

            await _stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
