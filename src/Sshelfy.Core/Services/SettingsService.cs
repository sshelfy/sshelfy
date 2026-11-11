using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Settings;

namespace Sshelfy.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly ISettingsStore _store;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _pendingSave;

    public SettingsService(ISettingsStore store)
    {
        _store = store;
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Update(Func<AppSettings, AppSettings> mutate)
    {
        AppSettings updated;
        lock (_lock)
        {
            updated = mutate(Current);
            if (updated == Current)
                return;
            Current = updated;
        }

        Changed?.Invoke(this, updated);
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
        await _store.SaveAsync(Current).ConfigureAwait(false);
    }

    private void ScheduleSave()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pendingSave?.Cancel();
            cts = _pendingSave = new CancellationTokenSource();
        }

        _ = SaveLaterAsync(cts);
    }

    private async Task SaveLaterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SaveDelay, cts.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_pendingSave == cts)
                    _pendingSave = null;
            }

            await _store.SaveAsync(Current, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change or flushed explicitly.
        }
    }
}
