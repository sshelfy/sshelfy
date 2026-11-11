using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;

namespace Sshelfy.Infrastructure.Storage;

internal sealed class JsonHostRepository : IHostRepository
{
    private readonly JsonFileStore<HostCatalogData> _store;

    public JsonHostRepository(StoragePaths paths)
    {
        _store = new JsonFileStore<HostCatalogData>(paths.File("hosts.json"), () => new HostCatalogData());
    }

    public Task<HostCatalogData> LoadAsync(CancellationToken cancellationToken = default) => _store.ReadAsync(cancellationToken);

    public Task SaveAsync(HostCatalogData data, CancellationToken cancellationToken = default) =>
        _store.WriteAsync(data, cancellationToken);
}
