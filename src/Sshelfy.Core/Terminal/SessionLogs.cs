namespace Sshelfy.Core.Terminal;

public sealed record SessionLogInfo(
    string FilePath,
    string HostName,
    DateTimeOffset Started,
    long SizeBytes,
    SessionLogFormat Format,
    bool IsEncrypted);

/// <summary>Creates, lists and reads session recordings.</summary>
public interface ISessionLogStore
{
    /// <summary>Folder the recordings are written to (from settings, or the default).</summary>
    string Directory { get; }

    /// <exception cref="Security.VaultLockedException">Encryption requested while the vault is locked.</exception>
    Task<ISessionLogSink> CreateAsync(string hostName, SessionLogFormat format, bool encrypt, CancellationToken cancellationToken = default);

    IReadOnlyList<SessionLogInfo> List();

    /// <summary>Whole recording as text; encrypted files need an unlocked vault.</summary>
    Task<string> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes recordings older than <paramref name="retentionDays"/> (0 keeps everything).</summary>
    int DeleteOlderThan(int retentionDays);

    void Delete(string filePath);
}
