using System.Text;
using Sshelfy.Core.History;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;
using Sshelfy.Infrastructure.Security;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Core.Tests;

public sealed class VaultedCommandHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sshelfy-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private StoragePaths Paths => new(_dir);

    private SecretVault NewVault() => new(Paths, new NoDeviceKey());

    private static CommandHistoryData Sample(string command) => new()
    {
        Hosts = { ["h1"] = new HostHistory { HostName = "web", Entries = [new CommandHistoryEntry { Command = command }] } },
    };

    [Fact]
    public async Task Without_a_vault_history_stays_plain_json()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        var store = new VaultedCommandHistoryStore(Paths, vault);

        await store.SaveAsync(Sample("uptime"));

        Assert.True(File.Exists(Path.Combine(_dir, "history.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "history.enc")));
        Assert.Equal("uptime", (await store.LoadAsync()).Hosts["h1"].Entries[0].Command);
    }

    [Fact]
    public async Task With_an_unlocked_vault_history_is_encrypted_and_the_plain_file_migrated()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        var store = new VaultedCommandHistoryStore(Paths, vault);
        await store.SaveAsync(Sample("cat /etc/shadow-secret-marker"));

        await vault.CreateAsync("master", rememberOnDevice: false);
        var migrated = await store.LoadAsync();
        await store.SaveAsync(migrated);

        Assert.False(File.Exists(Path.Combine(_dir, "history.json")));
        var raw = await File.ReadAllBytesAsync(Path.Combine(_dir, "history.enc"));
        Assert.DoesNotContain("secret-marker", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        var reopened = NewVault();
        await reopened.InitializeAsync();
        var lockedStore = new VaultedCommandHistoryStore(Paths, reopened);
        Assert.False(lockedStore.IsAvailable);
        Assert.Empty((await lockedStore.LoadAsync()).Hosts);

        Assert.True(await reopened.UnlockAsync("master", rememberOnDevice: false));
        Assert.True(lockedStore.IsAvailable);
        Assert.Equal("cat /etc/shadow-secret-marker", (await lockedStore.LoadAsync()).Hosts["h1"].Entries[0].Command);
    }

    [Fact]
    public async Task Commands_recorded_while_locked_are_merged_after_unlock()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        await vault.CreateAsync("master", rememberOnDevice: false);
        await new VaultedCommandHistoryStore(Paths, vault).SaveAsync(Sample("ls"));

        var reopened = NewVault();
        await reopened.InitializeAsync();
        var store = new VaultedCommandHistoryStore(Paths, reopened);
        var history = new CommandHistory(store, new Settings());
        await history.InitializeAsync();
        Assert.True(history.IsLocked);

        history.Record("h1", "web", "ls");
        history.Record("h1", "web", "df -h");

        var merged = new TaskCompletionSource();
        history.Changed += (_, key) =>
        {
            if (key.Length == 0)
                merged.TrySetResult();
        };
        Assert.True(await reopened.UnlockAsync("master", rememberOnDevice: false));
        await merged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(history.IsLocked);
        var entries = history.GetForHost("h1");
        Assert.Equal(["df -h", "ls"], entries.Select(e => e.Command));
        Assert.Equal(2, entries.Single(e => e.Command == "ls").UseCount);

        // The merge is saved in the background after Changed; finish it (and the debounced save of the
        // recorded commands) before Dispose removes the folder.
        await history.FlushAsync();
    }

    [Fact]
    public void Merge_keeps_latest_time_and_adds_counts()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-1);
        var stored = new CommandHistoryData
        {
            Hosts = { ["h"] = new HostHistory { HostName = "old name", Entries = [new CommandHistoryEntry { Command = "a", UseCount = 3, LastUsedAt = old }] } },
        };
        var recent = new CommandHistoryData
        {
            Hosts = { ["h"] = new HostHistory { HostName = "new name", Entries = [new CommandHistoryEntry { Command = "a", UseCount = 1 }] } },
        };

        var merged = CommandHistoryMerge.Merge(stored, recent).Hosts["h"];

        Assert.Equal("new name", merged.HostName);
        Assert.Equal(4, merged.Entries.Single().UseCount);
        Assert.True(merged.Entries.Single().LastUsedAt > old);
    }

    private sealed class NoDeviceKey : IDeviceKeyProtector
    {
        public bool IsSupported => false;

        public byte[] Protect(byte[] data, byte[] entropy) => throw new NotSupportedException();

        public byte[] Unprotect(byte[] data, byte[] entropy) => throw new NotSupportedException();
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();

        public event EventHandler<AppSettings>? Changed;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Update(Func<AppSettings, AppSettings> mutate)
        {
            Current = mutate(Current);
            Changed?.Invoke(this, Current);
        }

        public Task FlushAsync() => Task.CompletedTask;
    }
}
