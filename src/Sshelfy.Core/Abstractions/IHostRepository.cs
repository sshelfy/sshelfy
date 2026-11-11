using Sshelfy.Core.Hosts;

namespace Sshelfy.Core.Abstractions;

public interface IHostRepository
{
    Task<HostCatalogData> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(HostCatalogData data, CancellationToken cancellationToken = default);
}
