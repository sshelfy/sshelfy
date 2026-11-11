using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Storage;

internal sealed class JsonKnownHostsStore : IKnownHostsStore
{
    private readonly JsonFileStore<List<KnownHost>> _store;

    public JsonKnownHostsStore(StoragePaths paths)
    {
        _store = new JsonFileStore<List<KnownHost>>(paths.File("known_hosts.json"), () => []);
    }

    public async Task<IReadOnlyList<KnownHost>> FindAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var all = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(k => Matches(k, host, port)).ToList();
    }

    public async Task AddAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
    {
        var all = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        all.RemoveAll(k => Matches(k, knownHost.Host, knownHost.Port) && string.Equals(k.Algorithm, knownHost.Algorithm, StringComparison.Ordinal));
        all.Add(knownHost);
        await _store.WriteAsync(all, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceAllAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
    {
        var all = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        all.RemoveAll(k => Matches(k, knownHost.Host, knownHost.Port));
        all.Add(knownHost);
        await _store.WriteAsync(all, cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(KnownHost k, string host, int port) =>
        k.Port == port && string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase);
}
