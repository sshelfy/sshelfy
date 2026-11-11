using System.Security.Cryptography;

namespace Sshelfy.Core.Sftp;

/// <summary>What an external edit reports back; called on background threads.</summary>
public interface IExternalEditCallbacks
{
    /// <summary>The file changed on the server since it was opened (or last uploaded): overwrite it?</summary>
    Task<bool> ConfirmOverwriteAsync(string remotePath);

    void Uploaded(string remotePath);

    void Failed(string remotePath, string message);
}

/// <summary>
/// A remote file opened in a local editor: downloaded to a temp file, which is watched; every save
/// is uploaded back (debounced, skipped when the content did not change, with a check that nobody
/// else changed the server copy meanwhile).
/// </summary>
public sealed class ExternalEditSync : IAsyncDisposable
{
    internal static TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);

    private readonly ISftpSession _session;
    private readonly IExternalEditCallbacks _callbacks;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _uploading = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private byte[] _lastHash;
    private DateTimeOffset _remoteModified;
    private Timer? _pending;

    private ExternalEditSync(ISftpSession session, string remotePath, string localPath, byte[] hash, DateTimeOffset remoteModified, IExternalEditCallbacks callbacks)
    {
        _session = session;
        RemotePath = remotePath;
        LocalPath = localPath;
        _lastHash = hash;
        _remoteModified = remoteModified;
        _callbacks = callbacks;

        // Editors often save by writing a temp file and renaming it over the original, so watch the folder.
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Created += (_, _) => Schedule();
        _watcher.Renamed += (_, e) =>
        {
            if (string.Equals(e.FullPath, LocalPath, StringComparison.OrdinalIgnoreCase))
                Schedule();
        };
        _watcher.EnableRaisingEvents = true;
    }

    public string RemotePath { get; }

    /// <summary>The session saves go through (a reconnect makes a new one).</summary>
    public ISftpSession Session => _session;

    public string LocalPath { get; }

    /// <summary>Downloads <paramref name="entry"/> into its own folder under <paramref name="tempRoot"/> (keeping its name, so editors pick the right syntax) and starts watching.</summary>
    /// <exception cref="SftpOperationException">The download failed.</exception>
    public static async Task<ExternalEditSync> StartAsync(ISftpSession session, RemoteFileEntry entry, string tempRoot, IExternalEditCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var localPath = LocalNames.Child(folder, entry.Name);

        await using (var file = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await session.DownloadAsync(entry.FullPath, file, null, cancellationToken);

        var hash = await HashAsync(localPath, cancellationToken);
        var modified = (await session.StatAsync(entry.FullPath, cancellationToken))?.Modified ?? entry.Modified;
        return new ExternalEditSync(session, entry.FullPath, localPath, hash, modified, callbacks);
    }

    /// <summary>Waits for a scheduled upload to finish (for tests and for closing cleanly).</summary>
    public async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _pending, null) is { } timer)
        {
            await timer.DisposeAsync();
            await UploadAsync();
        }

        await _uploading.WaitAsync();
        _uploading.Release();
    }

    private void Schedule()
    {
        if (_stop.IsCancellationRequested)
            return;
        var timer = new Timer(_ => _ = UploadAsync(), null, Debounce, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _pending, timer)?.Dispose();
    }

    private async Task UploadAsync()
    {
        if (_stop.IsCancellationRequested)
            return;

        await _uploading.WaitAsync();
        try
        {
            var content = await ReadWithRetriesAsync();
            if (content is null)
                return; // deleted, or still locked by the editor; the next save tries again

            var hash = SHA256.HashData(content);
            if (hash.AsSpan().SequenceEqual(_lastHash))
                return;

            var remote = await _session.StatAsync(RemotePath, _stop.Token);
            // Declining keeps the conflict: the next save asks again rather than overwriting silently.
            if (remote is not null && remote.Modified != _remoteModified && !await _callbacks.ConfirmOverwriteAsync(RemotePath))
                return;

            using var stream = new MemoryStream(content);
            await _session.UploadAsync(stream, RemotePath, null, _stop.Token);
            _lastHash = hash;
            _remoteModified = (await _session.StatAsync(RemotePath, _stop.Token))?.Modified ?? _remoteModified;
            _callbacks.Uploaded(RemotePath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SftpOperationException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Runs from a timer: anything not reported here would be lost silently.
            _callbacks.Failed(RemotePath, ex.Message);
        }
        finally
        {
            _uploading.Release();
        }
    }

    private async Task<byte[]?> ReadWithRetriesAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(LocalPath))
                    return null;
                await using var file = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, _stop.Token);
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by the editor, or delete-pending during a "safe save" (replace by rename).
                await Task.Delay(200, _stop.Token);
            }
        }

        return null;
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.EnableRaisingEvents = false;
        await FlushAsync();
        await _stop.CancelAsync();
        _watcher.Dispose();
        _stop.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(LocalPath)!, recursive: true);
        }
        catch (IOException)
        {
            // Still open in the editor; the temp folder is cleaned up by the system.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
