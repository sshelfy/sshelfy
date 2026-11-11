using Sshelfy.Core.Abstractions;

namespace Sshelfy.Core.Snippets;

/// <summary>A reusable command (or multi-line script) that can be sent to a terminal.</summary>
public sealed record Snippet
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    /// <summary>Send Enter after the command, i.e. run it instead of only inserting it.</summary>
    public bool RunImmediately { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Values last entered for the snippet's <c>{{variables}}</c>, offered again next time.</summary>
    public IReadOnlyDictionary<string, string>? LastValues { get; init; }
}

public interface ISnippetLibrary
{
    IReadOnlyList<Snippet> Snippets { get; }

    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Snippet snippet);

    Task DeleteAsync(Guid id);

    /// <summary>Records that the snippet ran, remembering the variable values it ran with.</summary>
    Task MarkUsedAsync(Guid id, IReadOnlyDictionary<string, string>? values = null);
}

/// <remarks>Awaits keep the caller's context so <see cref="Changed"/> fires on the UI thread.</remarks>
public sealed class SnippetLibrary : ISnippetLibrary
{
    private readonly ISnippetRepository _repository;
    private List<Snippet> _snippets = [];

    public SnippetLibrary(ISnippetRepository repository)
    {
        _repository = repository;
    }

    public IReadOnlyList<Snippet> Snippets => _snippets;

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _snippets = await _repository.LoadAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(Snippet snippet)
    {
        var index = _snippets.FindIndex(s => s.Id == snippet.Id);
        if (index >= 0)
            _snippets[index] = snippet;
        else
            _snippets.Add(snippet);
        await PersistAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        if (_snippets.RemoveAll(s => s.Id == id) > 0)
            await PersistAsync();
    }

    public async Task MarkUsedAsync(Guid id, IReadOnlyDictionary<string, string>? values = null)
    {
        var index = _snippets.FindIndex(s => s.Id == id);
        if (index < 0)
            return;
        var snippet = _snippets[index];
        _snippets[index] = snippet with { LastUsedAt = DateTimeOffset.UtcNow, LastValues = values ?? snippet.LastValues };
        await _repository.SaveAsync(_snippets);
    }

    private async Task PersistAsync()
    {
        await _repository.SaveAsync(_snippets);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
