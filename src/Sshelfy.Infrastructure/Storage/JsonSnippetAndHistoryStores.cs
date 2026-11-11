using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Activity;
using Sshelfy.Core.History;
using Sshelfy.Core.Snippets;

namespace Sshelfy.Infrastructure.Storage;

internal sealed class JsonSnippetRepository : ISnippetRepository
{
    private readonly JsonFileStore<List<Snippet>> _store;

    public JsonSnippetRepository(StoragePaths paths)
    {
        _store = new JsonFileStore<List<Snippet>>(paths.File("snippets.json"), () => []);
    }

    public Task<List<Snippet>> LoadAsync(CancellationToken cancellationToken = default) => _store.ReadAsync(cancellationToken);

    public Task SaveAsync(IReadOnlyList<Snippet> snippets, CancellationToken cancellationToken = default) =>
        _store.WriteAsync(snippets.ToList(), cancellationToken);
}


internal sealed class JsonActivityStore : IActivityStore
{
    private readonly JsonFileStore<List<ActivityEntry>> _store;

    public JsonActivityStore(StoragePaths paths)
    {
        _store = new JsonFileStore<List<ActivityEntry>>(paths.File("activity.json"), () => []);
    }

    public Task<List<ActivityEntry>> LoadAsync(CancellationToken cancellationToken = default) => _store.ReadAsync(cancellationToken);

    public Task SaveAsync(IReadOnlyList<ActivityEntry> entries, CancellationToken cancellationToken = default) =>
        _store.WriteAsync(entries.ToList(), cancellationToken);
}
