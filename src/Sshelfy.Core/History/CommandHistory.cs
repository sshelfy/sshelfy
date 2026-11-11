using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Services;

namespace Sshelfy.Core.History;

/// <summary>A command run on a host; repeated runs update the same entry.</summary>
public sealed record CommandHistoryEntry
{
    public string Command { get; init; } = string.Empty;

    public DateTimeOffset LastUsedAt { get; init; } = DateTimeOffset.UtcNow;

    public int UseCount { get; init; } = 1;
}

/// <summary>Persisted history: per host key, most recent first.</summary>
public sealed record CommandHistoryData
{
    public Dictionary<string, HostHistory> Hosts { get; init; } = new(StringComparer.Ordinal);
}

public sealed record HostHistory
{
    public string HostName { get; init; } = string.Empty;

    public List<CommandHistoryEntry> Entries { get; init; } = [];
}

public interface ICommandHistory
{
    /// <summary>Raised on the recording thread after an entry was added, updated or removed.</summary>
    event EventHandler<string>? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a command for a host. Commands starting with a space are ignored (like bash's ignorespace).</summary>
    void Record(string hostKey, string hostName, string command);

    IReadOnlyList<CommandHistoryEntry> GetForHost(string hostKey);

    /// <summary>Most recent entries across all hosts.</summary>
    IReadOnlyList<(string HostKey, string HostName, CommandHistoryEntry Entry)> GetRecent(int count);

    void Remove(string hostKey, string command);

    void Clear(string hostKey);

    Task FlushAsync();

    /// <summary>True while saved history is encrypted and cannot be shown (vault locked).</summary>
    bool IsLocked => false;
}

/// <summary>In-memory command history with debounced persistence and a per-host size limit.</summary>
public sealed class CommandHistory : ICommandHistory
{
    private const int MaxPerHost = 500;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly ICommandHistoryStore _store;
    private readonly ISettingsService _settings;
    private readonly Lock _lock = new();
    private CommandHistoryData _data = new();
    private CancellationTokenSource? _pendingSave;

    // Saves already started (after a debounce, or after the vault opened); FlushAsync waits for them.
    private Task _savesInFlight = Task.CompletedTask;

    // The store could not be read when the history was loaded (encrypted, vault locked): what is in
    // memory are only new entries, to be merged with the stored ones once the store opens.
    private bool _partial;

    public CommandHistory(ICommandHistoryStore store, ISettingsService settings)
    {
        _store = store;
        _settings = settings;
        _store.AvailabilityChanged += (_, _) => _ = OnStoreAvailabilityChangedAsync();
    }

    public event EventHandler<string>? Changed;

    /// <summary>True while saved history is encrypted and cannot be shown (vault locked).</summary>
    public bool IsLocked
    {
        get
        {
            lock (_lock)
                return _partial;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var available = _store.IsAvailable;
        var data = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _data = data;
            _partial = !available;
        }
    }

    private async Task OnStoreAvailabilityChangedAsync()
    {
        if (!_store.IsAvailable)
            return;

        bool merge;
        lock (_lock)
            merge = _partial;

        if (merge)
        {
            var stored = await _store.LoadAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _data = CommandHistoryMerge.Merge(stored, _data);
                _partial = false;
            }
        }

        // Writes skipped while locked, or a plain file to move into the vault. Started before Changed is
        // raised, so a FlushAsync called from a Changed handler already waits for it.
        var save = SaveNowAsync();
        if (merge)
            Changed?.Invoke(this, string.Empty);
        await save.ConfigureAwait(false);
    }

    public void Record(string hostKey, string hostName, string command)
    {
        if (!_settings.Current.RememberCommandHistory || string.IsNullOrWhiteSpace(command) || command.StartsWith(' '))
            return;

        command = command.Trim();
        lock (_lock)
        {
            if (!_data.Hosts.TryGetValue(hostKey, out var host))
            {
                host = new HostHistory { HostName = hostName };
                _data.Hosts[hostKey] = host;
            }
            else if (host.HostName != hostName)
            {
                host = host with { HostName = hostName };
                _data.Hosts[hostKey] = host;
            }

            var index = host.Entries.FindIndex(e => e.Command == command);
            var entry = index >= 0
                ? host.Entries[index] with { LastUsedAt = DateTimeOffset.UtcNow, UseCount = host.Entries[index].UseCount + 1 }
                : new CommandHistoryEntry { Command = command };
            if (index >= 0)
                host.Entries.RemoveAt(index);
            host.Entries.Insert(0, entry);

            if (host.Entries.Count > MaxPerHost)
                host.Entries.RemoveRange(MaxPerHost, host.Entries.Count - MaxPerHost);
        }

        Changed?.Invoke(this, hostKey);
        ScheduleSave();
    }

    public IReadOnlyList<CommandHistoryEntry> GetForHost(string hostKey)
    {
        lock (_lock)
            return _data.Hosts.TryGetValue(hostKey, out var host) ? host.Entries.ToList() : [];
    }

    public IReadOnlyList<(string HostKey, string HostName, CommandHistoryEntry Entry)> GetRecent(int count)
    {
        lock (_lock)
        {
            return _data.Hosts
                .SelectMany(h => h.Value.Entries.Select(e => (h.Key, h.Value.HostName, e)))
                .OrderByDescending(x => x.e.LastUsedAt)
                .Take(count)
                .ToList();
        }
    }

    public void Remove(string hostKey, string command)
    {
        lock (_lock)
        {
            if (!_data.Hosts.TryGetValue(hostKey, out var host) || host.Entries.RemoveAll(e => e.Command == command) == 0)
                return;
        }

        Changed?.Invoke(this, hostKey);
        ScheduleSave();
    }

    public void Clear(string hostKey)
    {
        lock (_lock)
        {
            if (!_data.Hosts.Remove(hostKey))
                return;
        }

        Changed?.Invoke(this, hostKey);
        ScheduleSave();
    }

    /// <summary>Writes a pending change now and waits until every save already started has finished.</summary>
    public async Task FlushAsync()
    {
        CancellationTokenSource? pending;
        lock (_lock)
        {
            pending = _pendingSave;
            _pendingSave = null;
        }

        if (pending is not null)
        {
            await pending.CancelAsync().ConfigureAwait(false);
            await SaveNowAsync().ConfigureAwait(false);
        }

        Task inFlight;
        lock (_lock)
            inFlight = _savesInFlight;
        await inFlight.ConfigureAwait(false);
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

                await SaveNowAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private Task SaveNowAsync()
    {
        CommandHistoryData snapshot;
        lock (_lock)
        {
            snapshot = new CommandHistoryData
            {
                Hosts = _data.Hosts.ToDictionary(h => h.Key, h => h.Value with { Entries = [.. h.Value.Entries] }, StringComparer.Ordinal),
            };
        }

        var save = _store.SaveAsync(snapshot);
        lock (_lock)
            _savesInFlight = Task.WhenAll(_savesInFlight, save);
        return save;
    }
}
