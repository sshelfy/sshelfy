using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh;

internal sealed class SshNetSftpSessionFactory : ISftpSessionFactory
{
    private readonly IHostKeyVerifier _hostKeys;
    private readonly ILogger? _logger;

    public SshNetSftpSessionFactory(IHostKeyVerifier hostKeys, ILogger<SshNetSftpSessionFactory>? logger = null)
    {
        _hostKeys = hostKeys;
        _logger = logger;
    }

    public async Task<ISftpSession> ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default)
    {
        var (client, chain) = await SshNetConnector.ConnectAsync(
            target,
            _hostKeys,
            info => new SftpClient(info)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
                OperationTimeout = TimeSpan.FromSeconds(60),
                BufferSize = 64 * 1024,
            },
            cancellationToken,
            _logger).ConfigureAwait(false);
        return new SshNetSftpSession(client, chain, _logger);
    }
}

internal sealed class SshNetSftpSession : ISftpSession
{
    private readonly SftpClient _client;
    private readonly JumpChain? _chain;
    private readonly ILogger? _logger;
    private int _disconnected;

    public SshNetSftpSession(SftpClient client, JumpChain? chain = null, ILogger? logger = null)
    {
        _client = client;
        _chain = chain;
        _logger = logger;
        HomeDirectory = client.WorkingDirectory;
        _client.ErrorOccurred += (_, e) =>
        {
            if (Interlocked.Exchange(ref _disconnected, 1) == 0)
            {
                _logger?.LogWarning("SFTP session to {Host} dropped: {Error}", _client.ConnectionInfo.Host, e.Exception.Message);
                Disconnected?.Invoke(this, e.Exception.Message);
            }
        };
    }

    public string HomeDirectory { get; }

    public bool IsConnected => _client.IsConnected;

    public event EventHandler<string?>? Disconnected;

    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            var entries = new List<RemoteFileEntry>();
            await foreach (var file in _client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                    continue;
                entries.Add(await ToEntryAsync(file, cancellationToken).ConfigureAwait(false));
            }

            return (IReadOnlyList<RemoteFileEntry>)entries;
        });

    public Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            try
            {
                var attributes = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
                return (RemoteFileEntry?)new RemoteFileEntry(
                    RemotePath.Name(path), path, attributes.IsDirectory, attributes.IsSymbolicLink, attributes.Size,
                    attributes.LastWriteTimeUtc, Permissions(attributes));
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        });

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Guard(() => _client.CreateDirectoryAsync(path, cancellationToken));

    public Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
        Guard(() => _client.RenameFileAsync(from, to, cancellationToken));

    public Task DeleteAsync(RemoteFileEntry entry, CancellationToken cancellationToken = default) =>
        Guard(() => DeleteRecursiveAsync(entry, cancellationToken));

    public Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken = default) =>
        Guard(() => _client.DownloadFileAsync(
            remotePath,
            destination,
            progress is null ? null : new Progress<DownloadFileProgressReport>(r => progress.Report((long)r.TotalBytesDownloaded)),
            cancellationToken));

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default) =>
        Guard(() => _client.UploadFileAsync(
            source,
            remotePath,
            canOverride: true,
            progress is null ? null : new Progress<UploadFileProgressReport>(r => progress.Report((long)r.TotalBytesUploaded)),
            cancellationToken));

    public Task AppendAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            await using var remote = await _client.OpenAsync(remotePath, FileMode.Append, FileAccess.Write, cancellationToken).ConfigureAwait(false);
            await CopyAsync(source, remote, progress, cancellationToken).ConfigureAwait(false);
        });

    public Task DownloadFromAsync(string remotePath, long offset, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            await using var remote = await _client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            remote.Seek(offset, SeekOrigin.Begin);
            await CopyAsync(remote, destination, progress, cancellationToken).ConfigureAwait(false);
        });

    private static async Task CopyAsync(Stream from, Stream to, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }

        await to.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<RemoteFileDetails> GetDetailsAsync(string path, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            var a = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return new RemoteFileDetails(path, Mode(a), a.UserId, a.GroupId);
        });

    public Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            // ChangePermissions only takes the nine rwx bits; going through the attributes keeps setuid/setgid/sticky.
            var a = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            a.OwnerCanRead = UnixPermissions.Has(mode, 0, 0);
            a.OwnerCanWrite = UnixPermissions.Has(mode, 0, 1);
            a.OwnerCanExecute = UnixPermissions.Has(mode, 0, 2);
            a.GroupCanRead = UnixPermissions.Has(mode, 1, 0);
            a.GroupCanWrite = UnixPermissions.Has(mode, 1, 1);
            a.GroupCanExecute = UnixPermissions.Has(mode, 1, 2);
            a.OthersCanRead = UnixPermissions.Has(mode, 2, 0);
            a.OthersCanWrite = UnixPermissions.Has(mode, 2, 1);
            a.OthersCanExecute = UnixPermissions.Has(mode, 2, 2);
            a.IsUIDBitSet = (mode & UnixPermissions.SetUid) != 0;
            a.IsGroupIDBitSet = (mode & UnixPermissions.SetGid) != 0;
            a.IsStickyBitSet = (mode & UnixPermissions.Sticky) != 0;
            await Task.Run(() => _client.SetAttributes(path, a), cancellationToken).ConfigureAwait(false);
        });

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            var a = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            a.LastWriteTimeUtc = modified.UtcDateTime;
            a.LastAccessTimeUtc = modified.UtcDateTime;
            await Task.Run(() => _client.SetAttributes(path, a), cancellationToken).ConfigureAwait(false);
        });

    public Task SetOwnerAsync(string path, int userId, int groupId, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            var a = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            a.UserId = userId;
            a.GroupId = groupId;
            await Task.Run(() => _client.SetAttributes(path, a), cancellationToken).ConfigureAwait(false);
        });

    private static int Mode(SftpFileAttributes a)
    {
        bool[] bits = [a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute, a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute, a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute];
        var mode = 0;
        for (var i = 0; i < 9; i++)
        {
            if (bits[i])
                mode |= UnixPermissions.Bit(i / 3, i % 3);
        }

        if (a.IsUIDBitSet)
            mode |= UnixPermissions.SetUid;
        if (a.IsGroupIDBitSet)
            mode |= UnixPermissions.SetGid;
        if (a.IsStickyBitSet)
            mode |= UnixPermissions.Sticky;
        return mode;
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(Task.Run(() =>
        {
            try
            {
                _client.Disconnect();
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
            {
            }

            _client.Dispose();
            _chain?.Dispose();
        }));
    }

    private async Task DeleteRecursiveAsync(RemoteFileEntry entry, CancellationToken cancellationToken)
    {
        if (entry.IsDirectory && !entry.IsSymlink)
        {
            foreach (var child in await ListAsync(entry.FullPath, cancellationToken).ConfigureAwait(false))
                await DeleteRecursiveAsync(child, cancellationToken).ConfigureAwait(false);
            await _client.DeleteDirectoryAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _client.DeleteFileAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RemoteFileEntry> ToEntryAsync(ISftpFile file, CancellationToken cancellationToken)
    {
        var isDirectory = file.IsDirectory;
        if (file.IsSymbolicLink)
        {
            // Follow links so linked directories can be browsed; broken links show as files.
            try
            {
                var target = await _client.GetAttributesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                isDirectory = target.IsDirectory;
            }
            catch (SftpPathNotFoundException)
            {
            }
        }

        return new RemoteFileEntry(file.Name, file.FullName, isDirectory, file.IsSymbolicLink, file.Length,
            file.LastWriteTimeUtc, Permissions(file.Attributes));
    }

    private static string Permissions(SftpFileAttributes a) =>
        (a.IsDirectory ? "d" : a.IsSymbolicLink ? "l" : "-") + UnixPermissions.ToSymbolic(Mode(a));

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            _logger?.LogWarning("SFTP operation failed: {Error} ({Type})", ex.Message, ex.GetType().Name);
            throw translated;
        }
    }

    private async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            _logger?.LogWarning("SFTP operation failed: {Error} ({Type})", ex.Message, ex.GetType().Name);
            throw translated;
        }
    }

    private static Exception? Translate(Exception ex) => ex switch
    {
        SftpPermissionDeniedException => new SftpOperationException("Permission denied.", ex),
        SftpPathNotFoundException => new SftpOperationException("No such file or directory.", ex),
        SshConnectionException => new SftpOperationException("The connection was lost.", ex),
        SshException => new SftpOperationException(ex.Message, ex),
        ObjectDisposedException => new SftpOperationException("The session is closed.", ex),
        _ => null,
    };
}
