using Sshelfy.Core.Abstractions;
using Sshelfy.Core.History;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;

namespace Sshelfy.Core.Tests;

public sealed class CommandHistoryTests
{
    private readonly FakeSettings _settings = new();
    private readonly CommandHistory _history;

    public CommandHistoryTests()
    {
        _history = new CommandHistory(new MemoryStore(), _settings);
    }

    [Fact]
    public void Repeated_command_moves_to_top_and_counts_uses()
    {
        _history.Record("h1", "web", "ls");
        _history.Record("h1", "web", "df -h");
        _history.Record("h1", "web", "ls");

        var entries = _history.GetForHost("h1");
        Assert.Equal(["ls", "df -h"], entries.Select(e => e.Command));
        Assert.Equal(2, entries[0].UseCount);
    }

    [Fact]
    public void Commands_starting_with_space_are_not_recorded()
    {
        _history.Record("h1", "web", " export TOKEN=secret");

        Assert.Empty(_history.GetForHost("h1"));
    }

    [Fact]
    public void Nothing_is_recorded_when_disabled()
    {
        _settings.Update(s => s with { RememberCommandHistory = false });
        _history.Record("h1", "web", "ls");

        Assert.Empty(_history.GetForHost("h1"));
    }

    [Fact]
    public void Hosts_are_kept_separate_and_recent_merges_them()
    {
        _history.Record("h1", "web", "a");
        _history.Record("h2", "db", "b");

        Assert.Single(_history.GetForHost("h1"));
        Assert.Equal(2, _history.GetRecent(10).Count);
        Assert.Equal("b", _history.GetRecent(10)[0].Entry.Command);
    }

    [Fact]
    public void Remove_and_clear()
    {
        _history.Record("h1", "web", "a");
        _history.Record("h1", "web", "b");

        _history.Remove("h1", "a");
        Assert.Equal(["b"], _history.GetForHost("h1").Select(e => e.Command));

        _history.Clear("h1");
        Assert.Empty(_history.GetForHost("h1"));
    }

    private sealed class MemoryStore : ICommandHistoryStore
    {
        public Task<CommandHistoryData> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CommandHistoryData());

        public Task SaveAsync(CommandHistoryData data, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSettings : ISettingsService
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
