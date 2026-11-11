namespace Sshelfy.Core.Sftp;

public enum CompareStatus
{
    Same,
    OnlyLocal,
    OnlyRemote,
    LocalNewer,
    RemoteNewer,

    /// <summary>Same time, different size.</summary>
    Different,

    /// <summary>A file on one side and a folder on the other.</summary>
    TypeMismatch,
}

/// <summary>One file present on either side, by its path relative to the compared folders ("/"-separated).</summary>
public sealed record CompareItem(string RelativePath, CompareStatus Status, LocalFileEntry? Local, RemoteFileEntry? Remote)
{
    /// <summary>The direction that makes both sides equal by copying the newer or missing file; null when there is nothing to copy.</summary>
    public SyncDirection? SuggestedDirection => Status switch
    {
        CompareStatus.OnlyLocal or CompareStatus.LocalNewer => SyncDirection.Upload,
        CompareStatus.OnlyRemote or CompareStatus.RemoteNewer => SyncDirection.Download,
        _ => null,
    };
}

public enum SyncDirection
{
    Upload,
    Download,
}

/// <summary>Compares a local folder with a remote one, file by file, through all subfolders.</summary>
public static class FolderComparison
{
    /// <summary>SFTP reports whole seconds and FAT drives two-second steps.</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public const int MaxEntries = 50_000;

    /// <param name="skipped">Receives subfolders that could not be read (e.g. owned by root); the rest is still compared.</param>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxEntries"/> files.</exception>
    public static async Task<IReadOnlyList<CompareItem>> CompareAsync(
        ILocalFileSystem local, string localRoot, ISftpSession remote, string remoteRoot,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, ICollection<string>? skipped = null)
    {
        var localFiles = new Dictionary<string, LocalFileEntry>(StringComparer.Ordinal);
        var remoteFiles = new Dictionary<string, RemoteFileEntry>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        await Task.WhenAll(
            ListLocalAsync(local, localRoot, string.Empty, localFiles, progress, unreadable, cancellationToken),
            ListRemoteAsync(remote, remoteRoot, string.Empty, remoteFiles, progress, unreadable, cancellationToken));
        foreach (var folder in unreadable)
            skipped?.Add(folder);

        return Merge(localFiles, remoteFiles);
    }

    /// <summary>Pairs the two listings (keys are "/"-separated relative paths).</summary>
    public static IReadOnlyList<CompareItem> Merge(IReadOnlyDictionary<string, LocalFileEntry> local, IReadOnlyDictionary<string, RemoteFileEntry> remote)
    {
        var items = new List<CompareItem>();
        foreach (var path in local.Keys.Union(remote.Keys).Order(StringComparer.OrdinalIgnoreCase))
        {
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            items.Add(new CompareItem(path, Status(l, r), l, r));
        }

        // Inside a folder that exists on one side only, every file is new as well: keep only the folder.
        var onlyFolders = items.Where(i => i.Status is CompareStatus.OnlyLocal or CompareStatus.OnlyRemote && (i.Local?.IsDirectory ?? i.Remote!.IsDirectory))
            .Select(i => i.RelativePath + "/").ToList();
        return items.Where(i => !onlyFolders.Any(f => i.RelativePath.StartsWith(f, StringComparison.Ordinal)))
            .Where(i => !(i.Status == CompareStatus.Same && (i.Local?.IsDirectory ?? false)))
            .ToList();
    }

    private static CompareStatus Status(LocalFileEntry? local, RemoteFileEntry? remote)
    {
        if (local is null)
            return CompareStatus.OnlyRemote;
        if (remote is null)
            return CompareStatus.OnlyLocal;
        if (local.IsDirectory != remote.IsDirectory)
            return CompareStatus.TypeMismatch;
        if (local.IsDirectory)
            return CompareStatus.Same;

        var difference = local.Modified - remote.Modified;
        if (difference > TimeTolerance)
            return CompareStatus.LocalNewer;
        if (difference < -TimeTolerance)
            return CompareStatus.RemoteNewer;
        return local.Size == remote.Size ? CompareStatus.Same : CompareStatus.Different;
    }

    private static async Task ListLocalAsync(ILocalFileSystem fs, string folder, string prefix, Dictionary<string, LocalFileEntry> files, IProgress<string>? progress, List<string> unreadable, CancellationToken cancellationToken)
    {
        progress?.Report(folder);
        IReadOnlyList<LocalFileEntry> entries;
        try
        {
            entries = await fs.ListAsync(folder, cancellationToken);
        }
        catch (LocalFileException) when (prefix.Length > 0)
        {
            lock (unreadable)
                unreadable.Add(folder);
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = prefix + entry.Name;
            files[relative] = entry;
            if (files.Count > MaxEntries)
                throw new InvalidOperationException($"These folders hold more than {MaxEntries:N0} files; compare smaller folders.");
            // Junctions and folder links can loop or pull in a whole other tree, as symlinks do on the server.
            if (entry.IsDirectory && !IsLink(entry.FullPath))
                await ListLocalAsync(fs, entry.FullPath, relative + "/", files, progress, unreadable, cancellationToken);
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable: do not descend
        }
    }

    private static async Task ListRemoteAsync(ISftpSession session, string folder, string prefix, Dictionary<string, RemoteFileEntry> files, IProgress<string>? progress, List<string> unreadable, CancellationToken cancellationToken)
    {
        progress?.Report(folder);
        IReadOnlyList<RemoteFileEntry> entries;
        try
        {
            entries = await session.ListAsync(folder, cancellationToken);
        }
        catch (SftpOperationException) when (prefix.Length > 0)
        {
            lock (unreadable)
                unreadable.Add(folder);
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
                continue;
            var relative = prefix + entry.Name;
            files[relative] = entry;
            if (files.Count > MaxEntries)
                throw new InvalidOperationException($"These folders hold more than {MaxEntries:N0} files; compare smaller folders.");
            if (entry.IsDirectory && !entry.IsSymlink)
                await ListRemoteAsync(session, entry.FullPath, relative + "/", files, progress, unreadable, cancellationToken);
        }
    }
}

/// <summary>Turns chosen comparison items into transfers that overwrite the older copy.</summary>
public static class FolderSync
{
    /// <returns>The queued transfers.</returns>
    public static async Task<IReadOnlyList<TransferItem>> EnqueueAsync(
        IEnumerable<(CompareItem Item, SyncDirection Direction)> choices, TransferQueue queue, ISftpSession session, string hostName,
        string localRoot, string remoteRoot, CancellationToken cancellationToken = default)
    {
        var transfers = new List<TransferItem>();
        var createdRemote = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, direction) in choices)
        {
            var parent = item.RelativePath.Contains('/') ? item.RelativePath[..item.RelativePath.LastIndexOf('/')] : string.Empty;
            if (direction == SyncDirection.Upload && item.Local is { } local)
            {
                var remoteDirectory = parent.Length == 0 ? remoteRoot : RemotePath.Combine(remoteRoot, parent);
                await EnsureRemoteDirectoryAsync(session, remoteRoot, parent, createdRemote, cancellationToken);
                transfers.Add(queue.EnqueueUpload(session, hostName, local.FullPath, remoteDirectory, ConflictChoice.Overwrite));
            }
            else if (direction == SyncDirection.Download && item.Remote is { } remote)
            {
                // Each remote folder name is made safe on its way to a local path (see LocalNames).
                var localDirectory = parent.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(localRoot, LocalNames.Child);
                Directory.CreateDirectory(localDirectory);
                transfers.Add(queue.EnqueueDownload(session, hostName, remote, localDirectory, ConflictChoice.Overwrite));
            }
        }

        return transfers;
    }

    /// <summary>Creates the missing folders of <paramref name="relative"/> under the remote root, outermost first.</summary>
    private static async Task EnsureRemoteDirectoryAsync(ISftpSession session, string root, string relative, HashSet<string> created, CancellationToken cancellationToken)
    {
        var path = root;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = RemotePath.Combine(path, part);
            if (!created.Add(path))
                continue;
            if (await session.StatAsync(path, cancellationToken) is null)
                await session.CreateDirectoryAsync(path, cancellationToken);
        }
    }
}
