using Sshelfy.Core.Hosts;

namespace Sshelfy.Core.Sftp;

public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTimeOffset Modified,
    string Permissions);

/// <summary>An SFTP connection. Operations may run concurrently (SSH multiplexes requests).</summary>
public interface ISftpSession : IAsyncDisposable
{
    string HomeDirectory { get; }

    bool IsConnected { get; }

    event EventHandler<string?>? Disconnected;

    Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default);

    Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task RenameAsync(string from, string to, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, or a directory with all of its contents.</summary>
    Task DeleteAsync(RemoteFileEntry entry, CancellationToken cancellationToken = default);

    /// <param name="progress">Receives the total number of bytes transferred so far.</param>
    Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken = default);

    Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default);

    /// <summary>Appends <paramref name="source"/> (from its current position) to the end of an existing remote file.</summary>
    /// <param name="progress">Receives the number of bytes appended so far.</param>
    Task AppendAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default);

    /// <summary>Downloads a remote file starting at <paramref name="offset"/> (to finish an interrupted download).</summary>
    /// <param name="progress">Receives the number of bytes downloaded by this call.</param>
    Task DownloadFromAsync(string remotePath, long offset, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken = default);

    /// <summary>Permission bits and numeric owner/group.</summary>
    Task<RemoteFileDetails> GetDetailsAsync(string path, CancellationToken cancellationToken = default);

    /// <param name="mode">Permission bits including setuid/setgid/sticky (see <see cref="UnixPermissions"/>).</param>
    Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken = default);

    Task SetOwnerAsync(string path, int userId, int groupId, CancellationToken cancellationToken = default);

    /// <summary>Sets the modification time (transfers keep the source's, so a later folder compare sees equal files).</summary>
    Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface ISftpSessionFactory
{
    /// <exception cref="Ssh.SshConnectionFailedException">Connection or authentication failed.</exception>
    Task<ISftpSession> ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default);
}

public sealed class SftpOperationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>POSIX path helpers for remote paths (never use System.IO.Path for these).</summary>
public static class RemotePath
{
    public static string Combine(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public static string Parent(string path)
    {
        if (path is "/" or "")
            return "/";
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>Path segments for a breadcrumb: "/var/log" → ["/", "/var", "/var/log"].</summary>
    public static IReadOnlyList<(string Name, string Path)> Segments(string path)
    {
        var result = new List<(string, string)> { ("/", "/") };
        var current = string.Empty;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            result.Add((part, current));
        }

        return result;
    }

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\0');
}
