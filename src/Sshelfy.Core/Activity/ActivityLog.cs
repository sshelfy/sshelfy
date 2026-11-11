using Sshelfy.Core.Abstractions;

namespace Sshelfy.Core.Activity;

public enum ActivityKind
{
    Connected,
    Disconnected,
    ConnectionFailed,
    LoginFailed,
    HostKeyTrusted,
    HostKeyChanged,
    HostKeyRejected,
    TransferCompleted,
    TransferFailed,
    TunnelStarted,
    TunnelFailed,
}

public enum ActivityCategory
{
    Connections,
    Security,
    Transfers,
    Tunnels,
}

public sealed record ActivityEntry(DateTimeOffset Time, ActivityKind Kind, string Host, string Message)
{
    public ActivityCategory Category => Kind switch
    {
        ActivityKind.LoginFailed or ActivityKind.HostKeyTrusted or ActivityKind.HostKeyChanged or ActivityKind.HostKeyRejected => ActivityCategory.Security,
        ActivityKind.TransferCompleted or ActivityKind.TransferFailed => ActivityCategory.Transfers,
        ActivityKind.TunnelStarted or ActivityKind.TunnelFailed => ActivityCategory.Tunnels,
        _ => ActivityCategory.Connections,
    };

    public bool IsProblem => Kind is ActivityKind.ConnectionFailed or ActivityKind.LoginFailed or ActivityKind.HostKeyChanged
        or ActivityKind.HostKeyRejected or ActivityKind.TransferFailed or ActivityKind.TunnelFailed;
}

/// <summary>Local timeline of notable events (connections, logins, host keys, transfers, tunnels).</summary>
public interface IActivityLog
{
    /// <summary>Newest first.</summary>
    IReadOnlyList<ActivityEntry> Entries { get; }

    /// <summary>Raised on the thread that added the entry.</summary>
    event EventHandler<ActivityEntry>? Added;

    event EventHandler? Cleared;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Add(ActivityKind kind, string host, string message);

    void Clear();

    Task FlushAsync();
}

/// <summary>Thread-safe activity log, capped at <see cref="MaxEntries"/>, persisted with a short delay.</summary>
public sealed class ActivityLog : IActivityLog
{
    public const int MaxEntries = 2000;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly IActivityStore _store;
    private readonly Lock _lock = new();
    private List<ActivityEntry> _entries = [];
    private CancellationTokenSource? _pendingSave;

    public ActivityLog(IActivityStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ActivityEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToList();
        }
    }

    public event EventHandler<ActivityEntry>? Added;

    public event EventHandler? Cleared;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
            _entries = entries.OrderByDescending(e => e.Time).Take(MaxEntries).ToList();
    }

    public void Add(ActivityKind kind, string host, string message)
    {
        var entry = new ActivityEntry(DateTimeOffset.Now, kind, host, message);
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }

        Added?.Invoke(this, entry);
        ScheduleSave();
    }

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public async Task FlushAsync()
    {
        CancellationTokenSource? pending;
        lock (_lock)
        {
            pending = _pendingSave;
            _pendingSave = null;
        }

        if (pending is null)
            return;
        await pending.CancelAsync().ConfigureAwait(false);
        await _store.SaveAsync(Entries).ConfigureAwait(false);
    }

    private void ScheduleSave()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pendingSave?.Cancel();
            cts = _pendingSave = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDelay, cts.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_pendingSave == cts)
                        _pendingSave = null;
                }

                await _store.SaveAsync(Entries).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
