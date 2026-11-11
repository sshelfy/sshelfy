using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ActivityKind = Sshelfy.Core.Activity.ActivityKind;
using IActivityLog = Sshelfy.Core.Activity.IActivityLog;

namespace Sshelfy.Core.Sftp;

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One queued upload or download (a single file or a whole directory tree). Property changes are
/// raised on the context that enqueued the transfer, so UI bindings can observe it directly.
/// </summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    private TransferState _state = TransferState.Queued;
    private long _transferredBytes;
    private long _totalBytes;
    private string? _error;
    private string? _currentFile;
    private double _bytesPerSecond;

    internal TransferItem(TransferDirection direction, string localPath, string remotePath, bool isDirectory, ISftpSession session, string hostName)
    {
        Direction = direction;
        LocalPath = localPath;
        RemotePath = remotePath;
        IsDirectory = isDirectory;
        Session = session;
        HostName = hostName;
        Name = direction == TransferDirection.Upload ? Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar)) : Sftp.RemotePath.Name(remotePath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    public TransferDirection Direction { get; }

    public string Name { get; }

    public string LocalPath { get; }

    public string RemotePath { get; }

    public bool IsDirectory { get; }

    public string HostName { get; }

    internal ISftpSession Session { get; }

    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>An "apply to all" answer for the remaining conflicts of this transfer.</summary>
    internal ConflictChoice? RememberedChoice { get; set; }

    /// <summary>Failed or cancelled transfers can be started again; finished files are skipped and partial ones resumed.</summary>
    public bool CanRetry => State is TransferState.Failed or TransferState.Cancelled;

    public TransferState State
    {
        get => _state;
        internal set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public long TransferredBytes
    {
        get => _transferredBytes;
        internal set
        {
            if (Set(ref _transferredBytes, value))
                OnPropertyChanged(nameof(Progress));
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        internal set
        {
            if (Set(ref _totalBytes, value))
                OnPropertyChanged(nameof(Progress));
        }
    }

    /// <summary>0–100.</summary>
    public double Progress => TotalBytes <= 0 ? (State == TransferState.Completed ? 100 : 0) : Math.Min(100, TransferredBytes * 100.0 / TotalBytes);

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        internal set => Set(ref _bytesPerSecond, value);
    }

    public string? CurrentFile
    {
        get => _currentFile;
        internal set => Set(ref _currentFile, value);
    }

    public string? Error
    {
        get => _error;
        internal set => Set(ref _error, value);
    }

    public bool IsActive => State is TransferState.Queued or TransferState.Running;

    public bool IsFinished => !IsActive;

    public void Cancel()
    {
        if (IsActive)
            Cancellation.Cancel();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Runs uploads and downloads with limited concurrency. Must be used from a single
/// synchronization context (the UI thread); progress is marshalled back to it.
/// </summary>
public sealed class TransferQueue
{
    private const int MaxConcurrent = 3;
    private readonly Queue<TransferItem> _pending = new();
    private readonly ILogger _logger;
    private readonly IActivityLog? _activity;
    private readonly ITransferConflictResolver? _conflicts;
    private int _running;

    /// <param name="conflicts">Asked when a target exists; without it existing files are overwritten.</param>
    public TransferQueue(ILogger<TransferQueue>? logger = null, IActivityLog? activity = null, ITransferConflictResolver? conflicts = null)
    {
        _logger = logger ?? NullLogger<TransferQueue>.Instance;
        _activity = activity;
        _conflicts = conflicts;
    }

    public ObservableCollection<TransferItem> Items { get; } = [];

    public int ActiveCount => Items.Count(i => i.IsActive);

    /// <summary>Raised when a transfer finishes (successfully or not).</summary>
    public event EventHandler<TransferItem>? Finished;

    /// <param name="onConflict">What to do with existing files without asking (folder sync overwrites).</param>
    public TransferItem EnqueueUpload(ISftpSession session, string hostName, string localPath, string remoteDirectory, ConflictChoice? onConflict = null)
    {
        var isDirectory = Directory.Exists(localPath);
        var remote = RemotePath.Combine(remoteDirectory, Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar)));
        return Enqueue(new TransferItem(TransferDirection.Upload, localPath, remote, isDirectory, session, hostName) { RememberedChoice = onConflict });
    }

    /// <param name="onConflict">What to do with existing files without asking (folder sync overwrites).</param>
    public TransferItem EnqueueDownload(ISftpSession session, string hostName, RemoteFileEntry entry, string localDirectory, ConflictChoice? onConflict = null)
    {
        var local = LocalNames.Child(localDirectory, entry.Name);
        return Enqueue(new TransferItem(TransferDirection.Download, local, entry.FullPath, entry.IsDirectory, session, hostName) { RememberedChoice = onConflict });
    }

    /// <summary>Starts a failed or cancelled transfer again, resuming partial files and skipping complete ones.</summary>
    public TransferItem Retry(TransferItem item)
    {
        Items.Remove(item);
        var again = new TransferItem(item.Direction, item.LocalPath, item.RemotePath, item.IsDirectory, item.Session, item.HostName)
        {
            RememberedChoice = ConflictChoice.Resume,
        };
        return Enqueue(again);
    }

    /// <summary>Cancels every active transfer that uses <paramref name="session"/> (its tab is closing).</summary>
    public void CancelForSession(ISftpSession session)
    {
        foreach (var item in Items.Where(i => i.Session == session))
            item.Cancel();
    }

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToList())
            Items.Remove(item);
    }

    private TransferItem Enqueue(TransferItem item)
    {
        Items.Insert(0, item);
        _pending.Enqueue(item);
        Pump();
        return item;
    }

    private void Pump()
    {
        while (_running < MaxConcurrent && _pending.Count > 0)
        {
            var item = _pending.Dequeue();
            if (item.Cancellation.IsCancellationRequested)
            {
                item.State = TransferState.Cancelled;
                continue;
            }

            _running++;
            _ = RunAsync(item);
        }
    }

    private async Task RunAsync(TransferItem item)
    {
        item.State = TransferState.Running;
        var token = item.Cancellation.Token;
        var clock = Stopwatch.StartNew();
        long completedBytes = 0;

        // Progress<T> captures the current (UI) context, so item updates happen there. One instance
        // per file with a fixed base offset, so late callbacks from a finished file cannot overshoot.
        Progress<long> ProgressFrom(long baseOffset) => new(bytes =>
        {
            if (item.IsFinished)
                return;
            item.TransferredBytes = Math.Max(item.TransferredBytes, Math.Min(item.TotalBytes, baseOffset + bytes));
            var seconds = clock.Elapsed.TotalSeconds;
            if (seconds > 0.25)
                item.BytesPerSecond = item.TransferredBytes / seconds;
        });

        try
        {
            if (item.Direction == TransferDirection.Upload)
            {
                var files = await Task.Run(() => PlanUpload(item.LocalPath, item.RemotePath, item.IsDirectory), token);
                item.TotalBytes = files.Sum(f => f.Size);
                foreach (var directory in files.Where(f => f.IsDirectory))
                    await EnsureRemoteDirectoryAsync(item.Session, directory.Remote, token);

                foreach (var file in files.Where(f => !f.IsDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    item.CurrentFile = Path.GetFileName(file.Local);
                    var target = file.Remote;
                    long offset = 0;

                    if (await item.Session.StatAsync(target, token) is { IsDirectory: false } existing)
                    {
                        var conflict = new TransferConflict(TransferDirection.Upload, RemotePath.Name(target), target,
                            existing.Size, existing.Modified, file.Size, file.Modified,
                            CanResume: existing.Size > 0 && existing.Size < file.Size, item.IsDirectory);
                        switch (await DecideAsync(item, conflict, token))
                        {
                            case ConflictChoice.Skip:
                                completedBytes += file.Size;
                                item.TransferredBytes = completedBytes;
                                continue;
                            case ConflictChoice.KeepBoth:
                                var directory = RemotePath.Parent(target);
                                target = RemotePath.Combine(directory, await TransferNames.NextFreeAsync(RemotePath.Name(target),
                                    async name => await item.Session.StatAsync(RemotePath.Combine(directory, name), token) is not null));
                                break;
                            case ConflictChoice.Resume:
                                offset = existing.Size;
                                break;
                        }
                    }

                    await using var stream = new FileStream(file.Local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    if (offset > 0)
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        await item.Session.AppendAsync(stream, target, ProgressFrom(completedBytes + offset), token);
                    }
                    else
                    {
                        await item.Session.UploadAsync(stream, target, ProgressFrom(completedBytes), token);
                    }

                    await KeepModifiedAsync(item.Session, target, file.Modified, token);
                    completedBytes += file.Size;
                    item.TransferredBytes = completedBytes;
                }
            }
            else
            {
                var files = await PlanDownloadAsync(item.Session, item.RemotePath, item.LocalPath, item.IsDirectory, token);
                item.TotalBytes = files.Sum(f => f.Size);
                foreach (var directory in files.Where(f => f.IsDirectory))
                    Directory.CreateDirectory(directory.Local);

                foreach (var file in files.Where(f => !f.IsDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    item.CurrentFile = RemotePath.Name(file.Remote);
                    var target = file.Local;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    long offset = 0;

                    if (File.Exists(target))
                    {
                        var existing = new FileInfo(target);
                        var conflict = new TransferConflict(TransferDirection.Download, existing.Name, target,
                            existing.Length, existing.LastWriteTime, file.Size, file.Modified,
                            CanResume: existing.Length > 0 && existing.Length < file.Size, item.IsDirectory);
                        switch (await DecideAsync(item, conflict, token))
                        {
                            case ConflictChoice.Skip:
                                completedBytes += file.Size;
                                item.TransferredBytes = completedBytes;
                                continue;
                            case ConflictChoice.KeepBoth:
                                var directory = Path.GetDirectoryName(target)!;
                                target = Path.Combine(directory, await TransferNames.NextFreeAsync(existing.Name,
                                    name => Task.FromResult(File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name)))));
                                break;
                            case ConflictChoice.Resume:
                                File.Move(target, target + ".part", overwrite: true);
                                offset = existing.Length;
                                break;
                        }
                    }

                    // A ".part" left by an interrupted download is continued, unless the server copy changed since.
                    var partial = target + ".part";
                    if (offset == 0 && File.Exists(partial) && new FileInfo(partial) is { Length: > 0 } part && part.Length < file.Size && part.LastWriteTimeUtc >= file.Modified.UtcDateTime)
                        offset = part.Length;

                    await using (var stream = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        if (offset > 0)
                            await item.Session.DownloadFromAsync(file.Remote, offset, stream, ProgressFrom(completedBytes + offset), token);
                        else
                            await item.Session.DownloadAsync(file.Remote, stream, ProgressFrom(completedBytes), token);
                    }

                    File.Move(partial, target, overwrite: true);
                    if (file.Modified != default)
                        File.SetLastWriteTimeUtc(target, file.Modified.UtcDateTime);
                    LocalNames.MarkAsDownloaded(target);
                    completedBytes += file.Size;
                    item.TransferredBytes = completedBytes;
                }
            }

            item.CurrentFile = null;
            item.TransferredBytes = item.TotalBytes;
            item.State = TransferState.Completed;
            _logger.LogInformation("{Direction} of {Name} ({Host}) finished: {Bytes} bytes", item.Direction, item.Name, item.HostName, item.TotalBytes);
            _activity?.Add(ActivityKind.TransferCompleted, item.HostName,
                $"{(item.Direction == TransferDirection.Upload ? "Uploaded" : "Downloaded")} {item.Name} ({ByteSize.Format(item.TotalBytes)})");
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SftpOperationException or InvalidOperationException)
        {
            item.Error = ex.Message;
            item.State = TransferState.Failed;
            _logger.LogWarning("{Direction} of {Name} ({Host}) failed: {Error}", item.Direction, item.Name, item.HostName, ex.Message);
            _activity?.Add(ActivityKind.TransferFailed, item.HostName, $"{item.Direction} of {item.Name} failed: {ex.Message}");
        }
        finally
        {
            _running--;
            Finished?.Invoke(this, item);
            Pump();
        }
    }

    /// <summary>Applies a remembered "apply to all" answer, otherwise asks the resolver (or overwrites without one).</summary>
    private async Task<ConflictChoice> DecideAsync(TransferItem item, TransferConflict conflict, CancellationToken token)
    {
        ConflictChoice choice;
        if (item.RememberedChoice is { } remembered)
        {
            choice = remembered;
        }
        else if (_conflicts is null)
        {
            choice = ConflictChoice.Overwrite;
        }
        else
        {
            var decision = await _conflicts.ResolveAsync(conflict, token);
            if (decision.Choice == ConflictChoice.Cancel)
                throw new OperationCanceledException(token);
            if (decision.ApplyToAll)
                item.RememberedChoice = decision.Choice;
            choice = decision.Choice;
        }

        // "Resume" for everything: complete files are skipped, mismatched ones transferred again.
        if (choice == ConflictChoice.Resume && !conflict.CanResume)
            choice = conflict.ExistingSize == conflict.SourceSize ? ConflictChoice.Skip : ConflictChoice.Overwrite;
        return choice;
    }

    private sealed record PlannedFile(string Local, string Remote, long Size, bool IsDirectory, DateTimeOffset Modified = default);

    private static List<PlannedFile> PlanUpload(string local, string remote, bool isDirectory)
    {
        if (!isDirectory)
        {
            var info = new FileInfo(local);
            return [new PlannedFile(local, remote, info.Length, false, info.LastWriteTimeUtc)];
        }

        var result = new List<PlannedFile> { new(local, remote, 0, true) };
        foreach (var path in Directory.EnumerateFileSystemEntries(local, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(local, path).Replace(Path.DirectorySeparatorChar, '/');
            var target = RemotePath.Combine(remote, relative);
            if (Directory.Exists(path))
                result.Add(new PlannedFile(path, target, 0, true));
            else
                result.Add(new PlannedFile(path, target, new FileInfo(path).Length, false, File.GetLastWriteTimeUtc(path)));
        }

        // Parents before children.
        return result.OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Remote.Length).ToList();
    }

    private static async Task<List<PlannedFile>> PlanDownloadAsync(ISftpSession session, string remote, string local, bool isDirectory, CancellationToken token)
    {
        if (!isDirectory)
        {
            var stat = await session.StatAsync(remote, token);
            return [new PlannedFile(local, remote, stat?.Size ?? 0, false, stat?.Modified ?? default)];
        }

        var result = new List<PlannedFile> { new(local, remote, 0, true) };
        var stack = new Stack<(string Remote, string Local)>();
        stack.Push((remote, local));
        while (stack.Count > 0)
        {
            var (dir, localDir) = stack.Pop();
            foreach (var entry in await session.ListAsync(dir, token))
            {
                // Server names may hold "\" or ":" (ordinary characters on Linux): never let them leave the folder.
                var target = LocalNames.Child(localDir, entry.Name);
                if (entry.IsDirectory && entry.IsSymlink)
                {
                    continue; // a link to a folder can point back up the tree and never end
                }

                if (entry.IsDirectory)
                {
                    result.Add(new PlannedFile(target, entry.FullPath, 0, true));
                    stack.Push((entry.FullPath, target));
                }
                else if (!entry.IsSymlink)
                {
                    result.Add(new PlannedFile(target, entry.FullPath, entry.Size, false, entry.Modified));
                }
            }
        }

        return result;
    }

    /// <summary>Gives the uploaded copy the local file's time; a server that refuses only loses that detail.</summary>
    private static async Task KeepModifiedAsync(ISftpSession session, string path, DateTimeOffset modified, CancellationToken token)
    {
        if (modified == default)
            return;
        try
        {
            await session.SetModifiedAsync(path, modified, token);
        }
        catch (SftpOperationException)
        {
        }
    }

    private static async Task EnsureRemoteDirectoryAsync(ISftpSession session, string path, CancellationToken token)
    {
        var existing = await session.StatAsync(path, token);
        if (existing is null)
            await session.CreateDirectoryAsync(path, token);
        else if (!existing.IsDirectory)
            throw new SftpOperationException($"\"{path}\" exists and is not a directory.");
    }
}
